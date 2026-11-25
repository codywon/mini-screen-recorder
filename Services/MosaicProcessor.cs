using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace MiniRec.Services;

internal static class MosaicProcessor
{
    public static void ApplyPixelate(byte[] bgra, int width, int height, int blockSize)
    {
        if (width <= 0 || height <= 0) return;
        int bs = Math.Max(2, blockSize);
        int stride = width * 4;

        for (int by = 0; by < height; by += bs)
        {
            int yEnd = Math.Min(height, by + bs);
            for (int bx = 0; bx < width; bx += bs)
            {
                int xEnd = Math.Min(width, bx + bs);
                long sumB = 0, sumG = 0, sumR = 0;
                int count = 0;

                for (int y = by; y < yEnd; y++)
                {
                    int rowOffset = y * stride;
                    for (int x = bx; x < xEnd; x++)
                    {
                        int idx = rowOffset + x * 4;
                        sumB += bgra[idx];
                        sumG += bgra[idx + 1];
                        sumR += bgra[idx + 2];
                        count++;
                    }
                }

                if (count == 0) continue;
                byte avgB = (byte)(sumB / count);
                byte avgG = (byte)(sumG / count);
                byte avgR = (byte)(sumR / count);

                for (int y = by; y < yEnd; y++)
                {
                    int rowOffset = y * stride;
                    for (int x = bx; x < xEnd; x++)
                    {
                        int idx = rowOffset + x * 4;
                        bgra[idx] = avgB;
                        bgra[idx + 1] = avgG;
                        bgra[idx + 2] = avgR;
                        bgra[idx + 3] = 255;
                    }
                }
            }
        }
    }

    public static void ApplyBoxBlur(byte[] bgra, int width, int height, int radius)
    {
        if (radius <= 0 || width <= 1 || height <= 1) return;

        int r = Math.Min(radius, Math.Max(width, height));
        byte[] temp = new byte[bgra.Length];

        // 3-pass separable box blur approximates Gaussian blur in O(W*H).
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlurHorizontal(bgra, temp, width, height, r);
            BoxBlurVertical(temp, bgra, width, height, r);
        }
    }

    private static void BoxBlurHorizontal(byte[] src, byte[] dst, int width, int height, int radius)
    {
        int stride = width * 4;
        int window = radius * 2 + 1;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            int sumB = 0, sumG = 0, sumR = 0;

            for (int k = -radius; k <= radius; k++)
            {
                int sx = Math.Clamp(k, 0, width - 1);
                int idx = rowOffset + sx * 4;
                sumB += src[idx];
                sumG += src[idx + 1];
                sumR += src[idx + 2];
            }

            for (int x = 0; x < width; x++)
            {
                int dstIdx = rowOffset + x * 4;
                dst[dstIdx] = (byte)(sumB / window);
                dst[dstIdx + 1] = (byte)(sumG / window);
                dst[dstIdx + 2] = (byte)(sumR / window);
                dst[dstIdx + 3] = 255;

                int removeX = Math.Clamp(x - radius, 0, width - 1);
                int addX = Math.Clamp(x + radius + 1, 0, width - 1);
                int removeIdx = rowOffset + removeX * 4;
                int addIdx = rowOffset + addX * 4;

                sumB += src[addIdx] - src[removeIdx];
                sumG += src[addIdx + 1] - src[removeIdx + 1];
                sumR += src[addIdx + 2] - src[removeIdx + 2];
            }
        }
    }

    private static void BoxBlurVertical(byte[] src, byte[] dst, int width, int height, int radius)
    {
        int stride = width * 4;
        int window = radius * 2 + 1;

        for (int x = 0; x < width; x++)
        {
            int colOffset = x * 4;
            int sumB = 0, sumG = 0, sumR = 0;

            for (int k = -radius; k <= radius; k++)
            {
                int sy = Math.Clamp(k, 0, height - 1);
                int idx = sy * stride + colOffset;
                sumB += src[idx];
                sumG += src[idx + 1];
                sumR += src[idx + 2];
            }

            for (int y = 0; y < height; y++)
            {
                int dstIdx = y * stride + colOffset;
                dst[dstIdx] = (byte)(sumB / window);
                dst[dstIdx + 1] = (byte)(sumG / window);
                dst[dstIdx + 2] = (byte)(sumR / window);
                dst[dstIdx + 3] = 255;

                int removeY = Math.Clamp(y - radius, 0, height - 1);
                int addY = Math.Clamp(y + radius + 1, 0, height - 1);
                int removeIdx = removeY * stride + colOffset;
                int addIdx = addY * stride + colOffset;

                sumB += src[addIdx] - src[removeIdx];
                sumG += src[addIdx + 1] - src[removeIdx + 1];
                sumR += src[addIdx + 2] - src[removeIdx + 2];
            }
        }
    }

    public static Imaging.BitmapSource CreatePixelatedBitmap(ScreenSnapshot snapshot, CaptureRegion region, int blockSize)
    {
        byte[] bgra = snapshot.CropRegionBgraBytes(region, out int w, out int h);
        ApplyPixelate(bgra, w, h, blockSize);
        var bmp = Imaging.BitmapSource.Create(w, h, 96, 96, Media.PixelFormats.Bgr32, null, bgra, w * 4);
        bmp.Freeze();
        return bmp;
    }

    public static Imaging.BitmapSource CreateBlurredBitmap(ScreenSnapshot snapshot, CaptureRegion region, int radius)
    {
        byte[] bgra = snapshot.CropRegionBgraBytes(region, out int w, out int h);
        ApplyBoxBlur(bgra, w, h, radius);
        var bmp = Imaging.BitmapSource.Create(w, h, 96, 96, Media.PixelFormats.Bgr32, null, bgra, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
