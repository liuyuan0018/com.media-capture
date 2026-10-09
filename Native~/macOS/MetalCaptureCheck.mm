// Standalone native integration check. Its CPU-generated color chart is test input only.
#include "MetalCapture.mm"
#include <fstream>
#include <cmath>
static void WriteWav(const std::string& path, int samples) {
    std::ofstream out(path, std::ios::binary);
    auto u16=[&](uint16_t v){out.write((const char*)&v,2);};
    auto u32=[&](uint32_t v){out.write((const char*)&v,4);};
    out.write("RIFF",4);u32(36+samples*4);out.write("WAVEfmt ",8);u32(16);u16(1);u16(2);u32(48000);u32(192000);u16(4);u16(16);out.write("data",4);u32(samples*4);
    for(int i=0;i<samples;i++){int16_t v=(int16_t)(6000*sin(i*2*3.141592653589793*440/48000));u16(v);u16(v);}
}
int main(int argc,char** argv) { @autoreleasepool {
    if(argc<2)return 2;
    std::string prefix=argv[1]; bool abortCase=argc>2;
    id<MTLDevice> device=MTLCreateSystemDefaultDevice(); id<MTLCommandQueue> queue=[device newCommandQueue];
    MTLTextureDescriptor* desc=[MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatBGRA8Unorm width:640 height:360 mipmapped:NO];
    desc.storageMode=MTLStorageModeShared; id<MTLTexture> source=[device newTextureWithDescriptor:desc];
    std::string video=prefix+".video.mp4", audio=prefix+".wav", output=prefix+".mp4";
    Options opts{1,640,360,30,1,48000,8,20,2000,video.c_str()}; char error[2048]{};
    uint64_t handle=mcmetal_create((__bridge void*)source,&opts,error,sizeof(error));
    if(!handle){fprintf(stderr,"create: %s\n",error);return 1;}
    printf("hardware=%d\n",mcmetal_hardware(handle)); auto s=Find(handle);
    std::vector<uint32_t> pixels(640*360);
    for(int i=0;i<90;i++) {
        // Red top, blue bottom, moving white bar establishes orientation, channels and motion.
        for(int y=0;y<360;y++)for(int x=0;x<640;x++)pixels[y*640+x]=abs(x-i*7%640)<10 ? 0xffffffff : (y<180?0xffff0000:0xff0000ff);
        [source replaceRegion:MTLRegionMake2D(0,0,640,360) mipmapLevel:0 withBytes:pixels.data() bytesPerRow:640*4];
        auto slot=s->Acquire(); if(!slot){fprintf(stderr,"pool exhausted\n");return 1;}
        auto commands=[queue commandBuffer]; s->Copy(commands,source,slot,i*1600); [commands commit]; [commands waitUntilCompleted];
        std::this_thread::sleep_for(std::chrono::milliseconds(33));
        if(s->cancelled()){fprintf(stderr,"encode: %s\n",s->Error().c_str());return 1;}
        if(abortCase && i==9)break;
    }
    if(abortCase) {
        auto late=mcmetal_queue_frame(handle,(__bridge void*)source,16000);
        mcmetal_abort(handle); RenderEvent(0,late); // Stale event must be harmless.
    } else {
        WriteWav(audio,144000);
        if(!mcmetal_stop(handle,144000,audio.c_str(),output.c_str()))return 1;
    }
    auto begin=std::chrono::steady_clock::now(); Status status{};
    while(true){
        mcmetal_status(handle,&status,error,sizeof(error));
        if(status.state>=2)break;
        if(std::chrono::steady_clock::now()-begin>std::chrono::seconds(20)){fprintf(stderr,"stop timeout\n");return 1;}
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    printf("state=%d captured=%lld encoded=%lld duplicates=%lld dropped=%lld stopMs=%.1f error=%s\n",status.state,(long long)status.captured,(long long)status.encoded,(long long)status.duplicated,(long long)status.dropped,std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-begin).count(),error);
    bool success=status.state==(abortCase?4:2);
    if(!success)mcmetal_abort(handle);
    while(!mcmetal_destroy(handle))std::this_thread::sleep_for(std::chrono::milliseconds(5));
    return success?0:1;
}}
