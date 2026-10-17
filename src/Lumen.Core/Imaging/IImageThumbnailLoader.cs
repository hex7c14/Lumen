namespace Lumen.Core.Imaging;

/// <summary>
/// 快速缩略图加载。实现应尽量使用编码器的缩放解码能力（如 JPEG 的 1/2、1/4、1/8 采样），
/// 避免为队列缩略图解码整张大图；格式不支持缩放解码时回退为全尺寸解码再缩放。
/// </summary>
public interface IImageThumbnailLoader
{
    /// <summary>
    /// 加载长边不超过 <paramref name="maxDimension"/> 的缩略图（EXIF 方向已归一化）。
    /// </summary>
    ImagePixels LoadThumbnail(string path, int maxDimension);
}
