import AppKit
import AVFoundation
import CoreMedia
import Foundation

guard CommandLine.arguments.count >= 2 && CommandLine.arguments.count <= 4 else {
    fputs("usage: inspect_media.swift <media-file> [report.json] [waveform.png]\n", stderr)
    exit(2)
}

func fourCC(_ value: FourCharCode) -> String {
    let bytes: [UInt8] = [
        UInt8((value >> 24) & 0xff),
        UInt8((value >> 16) & 0xff),
        UInt8((value >> 8) & 0xff),
        UInt8(value & 0xff)
    ]
    return String(bytes: bytes, encoding: .ascii)?.trimmingCharacters(in: .whitespaces) ?? "unknown"
}

let url = URL(fileURLWithPath: CommandLine.arguments[1])
let asset = AVURLAsset(url: url)
guard let videoTrack = asset.tracks(withMediaType: .video).first,
      let audioTrack = asset.tracks(withMediaType: .audio).first else {
    fputs("expected one video track and one audio track\n", stderr)
    exit(3)
}

let videoCodec = videoTrack.formatDescriptions.first
    .map { fourCC(CMFormatDescriptionGetMediaSubType($0 as! CMFormatDescription)) } ?? "unknown"
let audioCodec = audioTrack.formatDescriptions.first
    .map { fourCC(CMFormatDescriptionGetMediaSubType($0 as! CMFormatDescription)) } ?? "unknown"
let videoSeconds = CMTimeGetSeconds(videoTrack.timeRange.duration)
let audioSeconds = CMTimeGetSeconds(audioTrack.timeRange.duration)

var audioSampleRate = 0.0
var audioChannels: UInt32 = 0
if let description = audioTrack.formatDescriptions.first,
   let basic = CMAudioFormatDescriptionGetStreamBasicDescription(description as! CMAudioFormatDescription) {
    audioSampleRate = basic.pointee.mSampleRate
    audioChannels = basic.pointee.mChannelsPerFrame
}

let reader = try AVAssetReader(asset: asset)
let audioOutput = AVAssetReaderTrackOutput(
    track: audioTrack,
    outputSettings: [
        AVFormatIDKey: kAudioFormatLinearPCM,
        AVLinearPCMIsFloatKey: true,
        AVLinearPCMBitDepthKey: 32,
        AVLinearPCMIsNonInterleaved: false,
        AVLinearPCMIsBigEndianKey: false
    ]
)
guard reader.canAdd(audioOutput) else {
    fputs("cannot decode audio track as float PCM\n", stderr)
    exit(4)
}
reader.add(audioOutput)
guard reader.startReading() else {
    fputs("audio reader failed to start: \(reader.error?.localizedDescription ?? "unknown")\n", stderr)
    exit(5)
}

var peak = 0.0
var sumSquares = 0.0
var decodedValues: Int64 = 0
let waveformWidth = 1200
var waveformPeaks = [Double](repeating: 0, count: waveformWidth)
let estimatedValues = max(
    1,
    Int64((audioSeconds * audioSampleRate * Double(max(1, audioChannels))).rounded())
)
while let sampleBuffer = audioOutput.copyNextSampleBuffer() {
    guard let block = CMSampleBufferGetDataBuffer(sampleBuffer) else { continue }
    let byteCount = CMBlockBufferGetDataLength(block)
    if byteCount <= 0 { continue }
    var bytes = [UInt8](repeating: 0, count: byteCount)
    let status = CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: byteCount, destination: &bytes)
    if status != kCMBlockBufferNoErr { continue }
    bytes.withUnsafeBytes { raw in
        for value in raw.bindMemory(to: Float.self) {
            let magnitude = abs(Double(value))
            peak = max(peak, magnitude)
            sumSquares += magnitude * magnitude
            let bin = min(waveformWidth - 1, Int(decodedValues * Int64(waveformWidth) / estimatedValues))
            waveformPeaks[bin] = max(waveformPeaks[bin], magnitude)
            decodedValues += 1
        }
    }
}
guard reader.status == .completed else {
    fputs("audio decode failed: \(reader.error?.localizedDescription ?? "unknown")\n", stderr)
    exit(6)
}

let rms = decodedValues > 0 ? sqrt(sumSquares / Double(decodedValues)) : 0
let result: [String: Any] = [
    "path": url.path,
    "containerDurationSeconds": CMTimeGetSeconds(asset.duration),
    "video": [
        "codec": videoCodec,
        "durationSeconds": videoSeconds,
        "width": Int(videoTrack.naturalSize.width),
        "height": Int(videoTrack.naturalSize.height),
        "nominalFrameRate": videoTrack.nominalFrameRate
    ],
    "audio": [
        "codec": audioCodec,
        "durationSeconds": audioSeconds,
        "sampleRate": audioSampleRate,
        "channels": audioChannels,
        "decodedValues": decodedValues,
        "peak": peak,
        "rms": rms,
        "peakDbFS": peak > 0 ? 20.0 * log10(peak) : -Double.infinity,
        "rmsDbFS": rms > 0 ? 20.0 * log10(rms) : -Double.infinity
    ],
    "avDurationDeltaSeconds": abs(videoSeconds - audioSeconds)
]
let json = try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys])
print(String(data: json, encoding: .utf8)!)
if CommandLine.arguments.count >= 3 {
    try json.write(to: URL(fileURLWithPath: CommandLine.arguments[2]), options: .atomic)
}

if CommandLine.arguments.count >= 4 {
    let waveformHeight = 240
    let image = NSImage(size: NSSize(width: waveformWidth, height: waveformHeight))
    image.lockFocus()
    NSColor(calibratedWhite: 0.06, alpha: 1).setFill()
    NSRect(x: 0, y: 0, width: waveformWidth, height: waveformHeight).fill()
    NSColor(calibratedRed: 0.25, green: 0.9, blue: 0.55, alpha: 1).setStroke()
    let path = NSBezierPath()
    path.lineWidth = 1
    let center = Double(waveformHeight) * 0.5
    for (x, value) in waveformPeaks.enumerated() {
        let amplitude = min(center - 2, value * center)
        path.move(to: NSPoint(x: CGFloat(x), y: CGFloat(center - amplitude)))
        path.line(to: NSPoint(x: CGFloat(x), y: CGFloat(center + amplitude)))
    }
    path.stroke()
    image.unlockFocus()
    guard let tiff = image.tiffRepresentation,
          let bitmap = NSBitmapImageRep(data: tiff),
          let png = bitmap.representation(using: .png, properties: [:]) else {
        fputs("failed to render waveform PNG\n", stderr)
        exit(7)
    }
    try png.write(to: URL(fileURLWithPath: CommandLine.arguments[3]), options: .atomic)
}
