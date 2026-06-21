namespace ReuMedCertificates.Application.Abstractions;

/// <summary>Склейка нескольких изображений (фото сторон справки) в один PDF.</summary>
public interface IDocumentAssembler
{
    Task<byte[]> ImagesToPdfAsync(IReadOnlyList<byte[]> images, CancellationToken cancellationToken = default);
}
