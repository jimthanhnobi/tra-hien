# P1-02 fixtures

Synthetic solid-color 32×24 images and one-second silent videos, generated locally
with FFmpeg 9.0.2; no people, customer content or external source assets.

```sh
ffmpeg -f lavfi -i color=c=red:s=32x24:r=2:d=1 -frames:v 1 -update 1 valid.png
ffmpeg -i valid.png -frames:v 1 -update 1 valid.jpg
ffmpeg -i valid.png -frames:v 1 valid.webp
ffmpeg -i valid.png -frames:v 1 valid.avif
ffmpeg -f lavfi -i color=c=blue:s=32x24:r=2:d=1 -an valid.mp4
ffmpeg -f lavfi -i color=c=blue:s=32x24:r=2:d=1 -an valid.mov
```

Tests read these exact bytes; regenerated files need not have identical encoding.
