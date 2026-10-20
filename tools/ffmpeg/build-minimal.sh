#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
root="$repo/out/dependencies/ffmpeg-minimal"
prefix="$root/prefix"
export PATH="$root/nasm/nasm-2.16.03:/mingw64/bin:/usr/bin:$PATH"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig:/mingw64/lib/pkgconfig"
jobs="${NUMBER_OF_PROCESSORS:-4}"
(( jobs > 8 )) && jobs=8
mkdir -p "$root/src" "$prefix"
cd "$root/src"
if [[ ! -d x264-b35605ace3ddf7c1a5d67a2eb553f034aef41d55 ]]; then tar -xf "$root/downloads/x264-b35605a.tar.bz2"; fi
if [[ ! -d lame-3.100 ]]; then tar -xf "$root/downloads/lame-3.100.tar.gz"; fi
if [[ ! -d ffmpeg-9.0.2 ]]; then tar -xf "$root/downloads/ffmpeg-9.0.2.tar.xz"; fi
cd "$root/src/x264-b35605ace3ddf7c1a5d67a2eb553f034aef41d55"
if [[ ! -f "$prefix/lib/libx264.a" ]]; then
    ./configure --prefix="$prefix" --host=x86_64-w64-mingw32 --enable-static --disable-cli --disable-opencl --extra-ldflags=-static
    make -j"$jobs"
    make install
fi
cd "$root/src/lame-3.100"
if [[ ! -f "$prefix/lib/libmp3lame.a" ]]; then
    ./configure --prefix="$prefix" --host=x86_64-w64-mingw32 --enable-static --disable-shared --disable-frontend --disable-decoder CFLAGS='-O2' LDFLAGS='-static'
    make -j"$jobs"
    make install
fi
cd "$root/src/ffmpeg-9.0.2"
./configure --prefix="$root/package" --arch=x86_64 --target-os=mingw32 \
    --enable-gpl --enable-version3 --enable-static --disable-shared --disable-debug \
    --disable-autodetect --enable-libx264 --enable-libmp3lame --enable-zlib \
    --pkg-config-flags=--static --extra-cflags="-I$prefix/include" \
    --extra-ldflags="-L$prefix/lib -static -static-libgcc" \
    --disable-doc --disable-ffplay --disable-ffprobe --disable-network \
    --disable-hwaccels --disable-d3d11va --disable-d3d12va --disable-dxva2 --disable-vulkan \
    --disable-encoders --enable-encoder=libx264,aac,libmp3lame,wrapped_avframe \
    --disable-decoders --enable-decoder=wrapped_avframe,pcm_s16le_planar,pcm_s24le_planar,pcm_s32le_planar,png,h264,mp3,mp3float,mp3adu,mp3adufloat,vorbis,opus,flac,aac,aac_fixed,alac,ac3,eac3,ape,wavpack,pcm_alaw,pcm_mulaw,pcm_u8,pcm_s8,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_f32le,pcm_f32be,pcm_f64le,pcm_f64be \
    --disable-muxers --enable-muxer=mp4,mov,mp3,null \
    --disable-demuxers --enable-demuxer=image2,png_pipe,concat,mov,mp3,wav,ogg,flac,aac,ac3,eac3,ape,wv \
    --disable-indevs --enable-indev=lavfi --disable-outdevs \
    --disable-filters --enable-filter=aformat,anull,anullsrc,aresample,atempo,adelay,color,format,loop,setpts,crop,trim,transpose,concat,scale,null \
    --disable-bsfs --enable-bsf=h264_mp4toannexb,aac_adtstoasc,extract_extradata
make -j"$jobs"
mkdir -p "$root/package/licenses/ffmpeg"
cp ffmpeg.exe "$root/package/ffmpeg.exe"
strip "$root/package/ffmpeg.exe"
cp COPYING.GPLv3 "$root/package/licenses/ffmpeg/FFmpeg-GPL-3.0.txt"
cp "$root/src/x264-b35605ace3ddf7c1a5d67a2eb553f034aef41d55/COPYING" "$root/package/licenses/ffmpeg/x264-COPYING.txt"
cp "$root/src/lame-3.100/COPYING" "$root/package/licenses/ffmpeg/LAME-COPYING.txt"
cp /mingw64/share/licenses/zlib/LICENSE "$root/package/licenses/ffmpeg/zlib-LICENSE.txt"
for license in COPYING.LIB COPYING.RUNTIME COPYING3 README; do
    cp "/mingw64/share/licenses/gcc-libs/$license" "$root/package/licenses/ffmpeg/GCC-$license.txt"
done
cp /mingw64/share/licenses/winpthreads/COPYING "$root/package/licenses/ffmpeg/winpthreads-COPYING.txt"
cp ffbuild/config.log "$root/config.log"
"$root/package/ffmpeg.exe" -version > "$root/package/licenses/ffmpeg/BUILD.txt"
"$root/package/ffmpeg.exe" -hide_banner -encoders > "$root/package/licenses/ffmpeg/ENCODERS.txt"
gcc --version >> "$root/package/licenses/ffmpeg/BUILD.txt"
pkg-config --modversion zlib >> "$root/package/licenses/ffmpeg/BUILD.txt"
pacman -Q mingw-w64-x86_64-gcc mingw-w64-x86_64-zlib >> "$root/package/licenses/ffmpeg/BUILD.txt"
