# Third-party notices / 第三方许可

The package's own C#, C++ and integration code is under [MIT](LICENSE). The FFmpeg DLLs have a separate license; the package's MIT declaration does not relicense them.

本包自身的 C#、C++ 和集成代码使用 MIT 许可。FFmpeg DLL 使用下列独立许可，不因 package 标记为 MIT 而改变。

## FFmpeg

- Project: https://ffmpeg.org/
- Source: https://github.com/FFmpeg/FFmpeg/tree/1a748fe2cd43e3ead22fafb1b5b7d77f153898a8
- Commit: `1a748fe2cd43e3ead22fafb1b5b7d77f153898a8` (8.1.2 branch).
- Libraries: `avcodec-62.dll`, `avformat-62.dll`, `avutil-60.dll`, `swresample-6.dll`.
- License: **GNU Lesser General Public License 2.1 or later**; copyright the FFmpeg contributors, as identified in the source files.
- Full license: [FFmpeg LGPL 2.1](Native~/Licenses/FFmpeg-LGPL-2.1.txt).
- Upstream license details: [FFmpeg LICENSE.md](Native~/Licenses/FFmpeg-LICENSE.md).
- Complete corresponding source: [FFmpeg source archive](Native~/ThirdPartySources/ffmpeg-1a748fe2cd.zip), included alongside the binaries in this package/repository.
- Build configuration: [ffmpeg-configure.sh](Native~/ffmpeg-configure.sh); source hashes and tool pins: [ffmpeg-dependency.json](Native~/ffmpeg-dependency.json).

FFmpeg source is unmodified. These are dynamically linked shared libraries built without `--enable-gpl`, `--enable-nonfree` or `--enable-version3`. No libx264, libx265 or external AAC encoder is linked. FFmpeg's native AAC encoder is used. Optional GPL source files remain in the complete upstream source archive but are not included in the library build.

FFmpeg 源码未修改，DLL 使用动态链接；构建不启用 GPL、nonfree 或 version3 选项，不链接 x264、x265 或外部 AAC 编码器。AAC 使用 FFmpeg 自带编码器。完整上游源码中的可选 GPL 文件没有编入这些 DLL。

The libraries may be replaced with ABI-compatible rebuilt versions. The package imposes no restriction on reverse engineering for debugging modifications to these LGPL libraries. They are supplied without warranty; see the license text for the full terms.

允许使用 ABI 兼容的重建版本替换库。本包不限制为调试这些 LGPL 库的修改而进行的逆向工程。库不提供担保，完整条款以许可文本为准。

## NVIDIA codec headers

- Project: https://github.com/FFmpeg/nv-codec-headers
- Version: `n13.0.19.0`, commit `e844e5b26f46bb77479f063029595293aa8f812d`.
- Complete source: [header archive](Native~/ThirdPartySources/nv-codec-headers-e844e5b26f.zip).
- Copyright and MIT-style permission notices: [header notices](Native~/Licenses/nv-codec-headers.txt).

These headers define the interfaces used to load NVIDIA's installed driver. The NVIDIA driver is not redistributed in this package. SDK 13.0 headers require a compatible driver (570 or newer according to the header release).

这些头文件定义调用 NVIDIA 驱动的接口，包中不分发驱动本身。按该头文件版本要求，需兼容的 570 或更新驱动。

## Redistributing a Player / 分发 Player

Keep the above notices and license texts with the application, and provide the corresponding FFmpeg/header source archives and build scripts with that distribution or an equivalent LGPL-compliant source distribution. `Native~` is ignored by Unity's asset importer: Unity does not automatically copy its contents into a Player. The distributor must preserve those materials separately.

分发应用时，请同时保留这些说明和完整许可证，并随应用提供对应 FFmpeg、头文件源码和构建脚本，或按 LGPL 要求提供等效的源码获取方式。Unity 忽略 `Native~`，不会自动把其中的源码与许可证复制进 Player；应用分发者需要另外保存并提供这些资料。

GNU Make and pkgconf are downloaded only for local building; they are not shipped as runtime dependencies. Git for Windows, Visual Studio and the Windows SDK are build prerequisites supplied by their respective vendors.
