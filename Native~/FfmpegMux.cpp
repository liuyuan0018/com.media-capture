#include <algorithm>
#include <atomic>
#include <fstream>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libswresample/swresample.h>
}

namespace
{
void Check(int value, const char* action)
{
    if (value >= 0) return;
    char error[AV_ERROR_MAX_STRING_SIZE]{};
    av_strerror(value, error, sizeof(error));
    throw std::runtime_error(std::string(action) + ": " + error);
}
struct MuxResources
{
    AVFormatContext* video = nullptr;
    AVFormatContext* audio = nullptr;
    AVFormatContext* output = nullptr;
    AVCodecContext* decoder = nullptr;
    AVCodecContext* encoder = nullptr;
    SwrContext* resampler = nullptr;
    AVPacket* videoPacket = av_packet_alloc();
    AVPacket* audioPacket = av_packet_alloc();
    AVPacket* encodedPacket = av_packet_alloc();
    AVFrame* decoded = av_frame_alloc();
    AVFrame* converted = av_frame_alloc();
    ~MuxResources()
    {
        av_packet_free(&videoPacket); av_packet_free(&audioPacket); av_packet_free(&encodedPacket);
        av_frame_free(&decoded); av_frame_free(&converted);
        avcodec_free_context(&decoder); avcodec_free_context(&encoder);
        swr_free(&resampler);
        avformat_close_input(&video); avformat_close_input(&audio);
        if (output) { avio_closep(&output->pb); avformat_free_context(output); }
    }
};
}

// WAV is already complete. Video packets are copied, never decoded/re-encoded.
void McMuxAudio(const std::string& videoPath, const std::string& audioPath, const std::string& outputPath,
    const std::atomic<bool>& cancelled)
{
    MuxResources resources;
    if (!resources.videoPacket || !resources.audioPacket || !resources.encodedPacket ||
        !resources.decoded || !resources.converted) throw std::bad_alloc();
    Check(avformat_open_input(&resources.video, videoPath.c_str(), nullptr, nullptr), "Open captured video");
    Check(avformat_find_stream_info(resources.video, nullptr), "Read video streams");
    Check(avformat_open_input(&resources.audio, audioPath.c_str(), nullptr, nullptr), "Open captured WAV");
    Check(avformat_find_stream_info(resources.audio, nullptr), "Read audio streams");
    int vi = av_find_best_stream(resources.video, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
    int ai = av_find_best_stream(resources.audio, AVMEDIA_TYPE_AUDIO, -1, -1, nullptr, 0);
    Check(vi, "Find video stream"); Check(ai, "Find audio stream");
    AVStream* videoInput = resources.video->streams[vi];
    AVCodecParameters* audioParameters = resources.audio->streams[ai]->codecpar;
    if (audioParameters->codec_id != AV_CODEC_ID_PCM_S16LE || audioParameters->ch_layout.nb_channels != 2)
        throw std::runtime_error("Recording mux expects stereo PCM16 WAV.");

    const AVCodec* audioCodec = avcodec_find_encoder(AV_CODEC_ID_AAC);
    if (!audioCodec) throw std::runtime_error("FFmpeg AAC encoder is unavailable.");
    resources.encoder = avcodec_alloc_context3(audioCodec);
    auto* encoder = resources.encoder;
    if (!encoder) throw std::bad_alloc();
    encoder->sample_rate = audioParameters->sample_rate;
    av_channel_layout_default(&encoder->ch_layout, 2);
    encoder->sample_fmt = AV_SAMPLE_FMT_FLTP;
    encoder->time_base = AVRational{1, encoder->sample_rate};
    encoder->bit_rate = 192000;
    encoder->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
    Check(avcodec_open2(encoder, audioCodec, nullptr), "Open AAC encoder");
    Check(swr_alloc_set_opts2(&resources.resampler, &encoder->ch_layout, encoder->sample_fmt,
        encoder->sample_rate, &encoder->ch_layout, AV_SAMPLE_FMT_S16, encoder->sample_rate, 0, nullptr),
        "Create audio sample converter");
    Check(swr_init(resources.resampler), "Initialize audio sample converter");
    Check(avformat_alloc_output_context2(&resources.output, nullptr, "mp4", outputPath.c_str()), "Create final MP4");
    AVStream* videoOutput = avformat_new_stream(resources.output, nullptr);
    AVStream* audioOutput = avformat_new_stream(resources.output, nullptr);
    if (!videoOutput || !audioOutput) throw std::bad_alloc();
    Check(avcodec_parameters_copy(videoOutput->codecpar, videoInput->codecpar), "Copy H.264 parameters");
    videoOutput->time_base = videoInput->time_base;
    audioOutput->time_base = encoder->time_base;
    Check(avcodec_parameters_from_context(audioOutput->codecpar, encoder), "Copy AAC parameters");
    Check(avio_open(&resources.output->pb, outputPath.c_str(), AVIO_FLAG_WRITE), "Open final MP4");
    AVDictionary* options = nullptr;
    av_dict_set(&options, "movflags", "faststart", 0);
    int result = avformat_write_header(resources.output, &options);
    av_dict_free(&options);
    Check(result, "Write final MP4 header");
    auto readVideo = [&]() {
        av_packet_unref(resources.videoPacket);
        while (true)
        {
            int r = av_read_frame(resources.video, resources.videoPacket);
            if (r == AVERROR_EOF) return false;
            Check(r, "Read H.264 packet");
            if (resources.videoPacket->stream_index == vi) return true;
            av_packet_unref(resources.videoPacket);
        }
    };
    auto receiveAudio = [&]() {
        while (true)
        {
            int r = avcodec_receive_packet(encoder, resources.encodedPacket);
            if (r == AVERROR_EOF || r == AVERROR(EAGAIN)) return;
            Check(r, "Receive AAC packet");
            av_packet_rescale_ts(resources.encodedPacket, encoder->time_base, audioOutput->time_base);
            resources.encodedPacket->stream_index = audioOutput->index;
            r = av_interleaved_write_frame(resources.output, resources.encodedPacket);
            av_packet_unref(resources.encodedPacket);
            Check(r, "Write AAC packet");
        }
    };
    bool videoAvailable = readVideo();
    bool audioFinished = false;
    int64_t audioSamples = 0;
    std::vector<uint8_t> pcm;
    pcm.reserve(encoder->frame_size * 4 * 2);
    while (videoAvailable || !audioFinished)
    {
        if (cancelled) throw std::runtime_error("Recording cancelled while writing MP4.");
        if (videoAvailable && (audioFinished || av_compare_ts(resources.videoPacket->dts,
            videoInput->time_base, audioSamples, encoder->time_base) <= 0))
        {
            av_packet_rescale_ts(resources.videoPacket, videoInput->time_base, videoOutput->time_base);
            resources.videoPacket->stream_index = videoOutput->index;
            Check(av_interleaved_write_frame(resources.output, resources.videoPacket), "Copy video packet");
            videoAvailable = readVideo();
            continue;
        }
        const size_t frameBytes = static_cast<size_t>(encoder->frame_size) * 4;
        bool eof = false;
        while (pcm.size() < frameBytes && !eof)
        {
            int r = av_read_frame(resources.audio, resources.audioPacket);
            if (r == AVERROR_EOF) { eof = true; break; }
            Check(r, "Read PCM packet");
            if (resources.audioPacket->stream_index == ai)
                pcm.insert(pcm.end(), resources.audioPacket->data, resources.audioPacket->data + resources.audioPacket->size);
            av_packet_unref(resources.audioPacket);
        }
        int samples = static_cast<int>(std::min(pcm.size(), frameBytes) / 4);
        if (!samples)
        {
            Check(avcodec_send_frame(encoder, nullptr), "Drain AAC encoder");
            receiveAudio();
            audioFinished = true;
            continue;
        }
        av_frame_unref(resources.converted);
        auto* frame = resources.converted;
        frame->format = encoder->sample_fmt;
        frame->sample_rate = encoder->sample_rate;
        Check(av_channel_layout_copy(&frame->ch_layout, &encoder->ch_layout), "Set audio layout");
        frame->nb_samples = samples;
        frame->pts = audioSamples;
        Check(av_frame_get_buffer(frame, 0), "Allocate AAC input buffer");
        const uint8_t* input[] = {pcm.data()};
        Check(swr_convert(resources.resampler, frame->data, samples, input, samples), "Convert PCM samples");
        Check(avcodec_send_frame(encoder, frame), "Encode AAC frame");
        receiveAudio();
        audioSamples += samples;
        pcm.erase(pcm.begin(), pcm.begin() + samples * 4);
    }
    Check(av_write_trailer(resources.output), "Finish final MP4");
}
