namespace Lumen.Core.Tests;

/// <summary>测试用临时目录，Dispose 时尽力删除。</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lumen-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateFile(string relativePath, string content = "")
    {
        string fullPath = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public string CreateBytes(string relativePath, byte[] content)
    {
        string fullPath = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    public string Combine(string relativePath) => System.IO.Path.Combine(Path, relativePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}

internal static class TestImages
{
    /// <summary>生成一张确定性的渐变 + 棋盘测试图。</summary>
    public static Imaging.ImagePixels CreateGradient(int width, int height, byte alpha = 255)
    {
        var image = new Imaging.ImagePixels(width, height);
        byte[] pixels = image.Pixels;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = ((y * width) + x) * Imaging.ImagePixels.BytesPerPixel;
                pixels[index] = (byte)((x * 255) / Math.Max(1, width - 1));
                pixels[index + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                pixels[index + 2] = (byte)(((x / 4) + (y / 4)) % 2 == 0 ? 32 : 224);
                pixels[index + 3] = alpha;
            }
        }

        return image;
    }
}
