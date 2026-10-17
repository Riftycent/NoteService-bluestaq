using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.Domain.Abstractions;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;
using NoteService.Infrastructure.Storage;

namespace NoteService.API.Features.Attachments;

public sealed record AttachmentDto(Guid Id, Guid NoteId, string FileName, string ContentType, long SizeBytes, Guid UploadedBy, DateTimeOffset UploadedAt);

public sealed record AttachmentDownload(Stream Content, string FileName, string ContentType);

public interface IAttachmentService
{
    Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid teamId, Guid noteId, CancellationToken ct);

    Task<AttachmentDto> UploadAsync(Guid teamId, Guid noteId, string fileName, string contentType, long length, Stream content, CancellationToken ct);

    Task<AttachmentDownload> DownloadAsync(Guid teamId, Guid attachmentId, CancellationToken ct);

    Task DeleteAsync(Guid teamId, Guid attachmentId, CancellationToken ct);
}

public sealed class AttachmentService(
    NoteDbContext db,
    INoteAccess noteAccess,
    IBlobStore blobs,
    ICurrentUser currentUser,
    TimeProvider time,
    IOptions<AttachmentOptions> options) : IAttachmentService
{
    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid teamId, Guid noteId, CancellationToken ct)
    {
        await noteAccess.RequireAsync(teamId, noteId, NoteAccess.Read, ct: ct);
        var attachments = await db.Attachments.AsNoTracking().Where(a => a.NoteId == noteId).OrderBy(a => a.UploadedAt).ToListAsync(ct);
        return attachments.Select(ToDto).ToList();
    }

    public async Task<AttachmentDto> UploadAsync(Guid teamId, Guid noteId, string fileName, string contentType, long length, Stream content, CancellationToken ct)
    {
        var userId = currentUser.RequireId();
        await noteAccess.RequireAsync(teamId, noteId, NoteAccess.Edit, ct: ct);

        var settings = options.Value;
        if (length <= 0)
        {
            throw new ValidationFailedException("file", "The file is empty.");
        }

        if (length > settings.MaxBytes)
        {
            throw new ValidationFailedException("file", $"The file exceeds the maximum size of {settings.MaxBytes} bytes.");
        }

        var mediaType = NormalizeContentType(contentType);
        if (!settings.AllowedContentTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ValidationFailedException("file", $"Files of type '{mediaType}' are not allowed.");
        }

        var id = Guid.CreateVersion7();
        var attachment = new Attachment
        {
            Id = id,
            NoteId = noteId,
            FileName = SanitizeFileName(fileName),
            ContentType = mediaType,
            SizeBytes = length,
            StorageKey = Attachment.BuildStorageKey(teamId, noteId, id),
            UploadedBy = userId,
            UploadedAt = time.GetUtcNow()
        };

        await blobs.SaveAsync(attachment.StorageKey, content, ct);
        try
        {
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await blobs.DeleteAsync(attachment.StorageKey, CancellationToken.None);
            throw;
        }

        return ToDto(attachment);
    }

    public async Task<AttachmentDownload> DownloadAsync(Guid teamId, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await FindAsync(attachmentId, ct);
        await noteAccess.RequireAsync(teamId, attachment.NoteId, NoteAccess.Read, ct: ct);

        var stream = await blobs.OpenReadAsync(attachment.StorageKey, ct) ?? throw new NotFoundException("The attachment's file is missing.");
        return new AttachmentDownload(stream, attachment.FileName, attachment.ContentType);
    }

    public async Task DeleteAsync(Guid teamId, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await FindAsync(attachmentId, ct);
        await noteAccess.RequireAsync(teamId, attachment.NoteId, NoteAccess.Edit, ct: ct);

        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(ct);
        await blobs.DeleteAsync(attachment.StorageKey, ct);
    }

    private async Task<Attachment> FindAsync(Guid attachmentId, CancellationToken ct) =>
        await db.Attachments.FirstOrDefaultAsync(a => a.Id == attachmentId, ct) ?? throw new NotFoundException("Attachment not found.");

    internal static string NormalizeContentType(string? contentType)
    {
        var mediaType = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        return mediaType.Length == 0 ? "application/octet-stream" : mediaType;
    }

    /// <summary>Keeps only the leaf name and strips characters that are unsafe in headers or file systems.</summary>
    internal static string SanitizeFileName(string? fileName)
    {
        var leaf = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        var cleaned = new string(leaf.Where(c => !char.IsControl(c) && Path.GetInvalidFileNameChars().Contains(c) == false && c != '"').ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return "attachment";
        }

        return cleaned.Length > 255 ? cleaned[..255] : cleaned;
    }

    private static AttachmentDto ToDto(Attachment a) => new(a.Id, a.NoteId, a.FileName, a.ContentType, a.SizeBytes, a.UploadedBy, a.UploadedAt);
}

[Route(ApiRoutes.Base)]
[TeamAuthorize(TeamRole.Viewer)]
public sealed class AttachmentsController(IAttachmentService attachments) : ApiControllerBase
{
    [HttpGet("teams/{teamId:guid}/notes/{noteId:guid}/attachments")]
    public async Task<ActionResult<IReadOnlyList<AttachmentDto>>> List(Guid teamId, Guid noteId, CancellationToken ct) =>
        Ok(await attachments.ListAsync(teamId, noteId, ct));

    [HttpPost("teams/{teamId:guid}/notes/{noteId:guid}/attachments")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<AttachmentDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AttachmentDto>> Upload(Guid teamId, Guid noteId, IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var attachment = await attachments.UploadAsync(teamId, noteId, file.FileName, file.ContentType, file.Length, stream, ct);
        return CreatedAtAction(nameof(Download), new { teamId, attachmentId = attachment.Id }, attachment);
    }

    [HttpGet("teams/{teamId:guid}/attachments/{attachmentId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Download(Guid teamId, Guid attachmentId, CancellationToken ct)
    {
        var file = await attachments.DownloadAsync(teamId, attachmentId, ct);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpDelete("teams/{teamId:guid}/attachments/{attachmentId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid teamId, Guid attachmentId, CancellationToken ct)
    {
        await attachments.DeleteAsync(teamId, attachmentId, ct);
        return NoContent();
    }
}
