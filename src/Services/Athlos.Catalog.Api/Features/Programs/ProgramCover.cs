using Athlos.Catalog.Api.Data;
using Athlos.Catalog.Api.Media;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Programs;

// A program's cover image. The request body is the image itself (JPEG, PNG or WebP, at most 2 MB).
// The blob path is built from the tenant in ITenantContext and the program's id, never from input.
public static class ProgramCover
{
    public static void MapProgramCover(this IEndpointRouteBuilder app)
    {
        app.MapPut("/programs/{programId:guid}/cover", Upload)
            .Accepts<IFormFile>(ImageType.Jpeg.ContentType, ImageType.Png.ContentType, ImageType.Webp.ContentType)
            .WithName("UploadProgramCover")
            .WithTags("Programs");
        app.MapDelete("/programs/{programId:guid}/cover", Delete)
            .WithName("DeleteProgramCover")
            .WithTags("Programs");
    }

    private static async Task<Results<Ok<ProgramDetails>, NotFound, ProblemHttpResult>> Upload(
        Guid programId, HttpRequest request, ITenantContext tenant, CatalogDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        var program = await db.Programs.SingleOrDefaultAsync(p => p.Id == programId, cancellationToken);
        if (program is null)
        {
            return TypedResults.NotFound();
        }

        if (request.ContentLength > MediaStore.MaxImageBytes)
        {
            return TooLarge();
        }

        // Read at most one byte more than allowed, whatever Content-Length claimed.
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MediaStore.MaxImageBytes)
        {
            return TooLarge();
        }

        var bytes = buffer.ToArray();
        if (ImageType.Detect(bytes) is not { } type)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "The cover must be a JPEG, PNG or WebP image.");
        }

        var previous = program.CoverImagePath;
        program.CoverImagePath = MediaStore.CoverPath(tenant.TenantId!.Value, program.Id, type);
        await media.UploadAsync(program.CoverImagePath, BinaryData.FromBytes(bytes), type, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        if (previous is not null)
        {
            await media.DeleteAsync(previous, cancellationToken);
        }

        return TypedResults.Ok(await ProgramDetails.FromAsync(program, media, cancellationToken));
    }

    private static async Task<Results<NoContent, NotFound>> Delete(
        Guid programId, CatalogDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        var program = await db.Programs.SingleOrDefaultAsync(p => p.Id == programId, cancellationToken);
        if (program is null)
        {
            return TypedResults.NotFound();
        }

        if (program.CoverImagePath is { } path)
        {
            program.CoverImagePath = null;
            await db.SaveChangesAsync(cancellationToken);
            await media.DeleteAsync(path, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static ProblemHttpResult TooLarge() =>
        TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The cover image can be at most 2 MB.");
}
