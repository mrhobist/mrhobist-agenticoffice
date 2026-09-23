using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Application.Abstractions;

/// <summary>Yuklenen ama henuz bir ise baglanmamis ek. <see cref="Id"/> <c>POST /runs</c> govdesinde doner.</summary>
public sealed record StagedAttachment(string Id, string Name, string MediaType, long Size, AttachmentKind Kind);

/// <summary>
/// Ek dosyalari: <c>data/attachments/</c> (docs/DOMAIN.md → Ekler). Iki asama: yukleme <c>_staging/</c>'e yazar (is henuz yok),
/// is olusurken <see cref="ClaimAsync"/> dosyalari <c>{runId}/</c>'ye tasir. Dosya I/O burada, kurallar
/// <see cref="AttachmentRules"/>'ta; tur/boyut denetimi yazmadan ONCE yapilir.
/// </summary>
public interface IAttachmentStore
{
    /// <summary>Denetler (<see cref="AttachmentRules.Check"/>) ve gecici alana yazar. Bir gunden eski gecici dosyalar bu sirada silinir.</summary>
    Task<StagedAttachment> StageAsync(string fileName, long size, Stream content, CancellationToken ct);

    /// <summary>
    /// Gecici ekleri calismanin dizinine tasir. Word icin metin cikarilir, yanina <c>.txt</c> yazilir. Bilinmeyen kimlik
    /// <c>attachment.not_found</c>; hicbir dosya yarim tasinmaz (once hepsi denetlenir).
    /// </summary>
    Task<IReadOnlyList<RunAttachment>> ClaimAsync(string runId, IReadOnlyList<string> stagedIds, CancellationToken ct);

    /// <summary>Calismanin ek dizini (mutlak). Ajanin okuma izni bu dizine verilir.</summary>
    string DirectoryOf(string runId);

    /// <summary>Indirme icin dosyanin mutlak yolu; ad calismanin eklerinden biri degilse ya da dosya yoksa null.</summary>
    string? PathOf(string runId, string fileName);

    /// <summary>Calismanin ek dizinini siler (calisma silinirken). Yoksa sessiz.</summary>
    void DeleteRun(string runId);
}
