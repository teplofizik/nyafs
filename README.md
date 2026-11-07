# NyaFs

Pure C# filesystem readers/builders and a small scripting core for working with
firmware images (kernel, ramfs/rootfs, device tree and composite containers).

## Features

- Filesystems: CPIO, TAR, EXT2/3/4, SquashFS, CramFS (LE/BE), ROMFS, FAT12/16/32,
  F2FS, UBIFS, LittleFS/SPIFFS (plugins).
- Image formats: raw/compressed kernels, FIT, Android and legacy images,
  device trees, vendor containers.
- Extended attributes (xattr), Unix permissions, uid/gid and symlinks.
- `.nyapkg` packages with TAR/PAX xattrs and filtering.
- Plugin architecture for filesystems, compressors, images and commands,
  including optional remote transports (SSH/SCP/SFTP/FTP) and services
  (SFTP/NFS/TFTP servers).

## Targets

`netstandard2.0`, `net8.0`, `net9.0`, `net10.0`.

## Usage

Reference the `NyaFs` NuGet package and use `NyaFs.Processor.ImageProcessor`,
the filesystem readers/builders, or the scripting API. See the consuming
`nyaimagetool` repository for command-line examples and manual testing notes.

## Build and test

```bash
dotnet build src/NyaFs/NyaFs.csproj -c Release
dotnet pack src/NyaFs/NyaFs.csproj -c Release -o <local-feed>
```

The full application tests live in the `nyaimagetool` repository.
