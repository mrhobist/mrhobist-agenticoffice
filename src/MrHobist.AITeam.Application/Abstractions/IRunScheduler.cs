using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Application.Abstractions;

/// <summary>
/// Uzun isi (analiz, dagitim) is kanalina birakir; hemen doner. Uygulamasi host tarafindadir (Api: <c>RunScheduler</c> → <c>JobChannel</c>).
/// RunService bunu iki yerde kullanir: ajani bosalan "bekleyen" calismalari uyandirmak ve yeniden baslatmada bekleyenleri
/// kuyruga geri koymak. Uclar da ayni kapidan yazar; hangi adimin hangi isi dogurdugu tek yerde kalir.
/// </summary>
public interface IRunScheduler
{
    /// <summary><see cref="RunStep.Analyze"/> → analiz isi · <see cref="RunStep.Dispatch"/> → dagitim isi · <see cref="RunStep.Approval"/> → is yok (kullanici karar verir).</summary>
    void Schedule(string runId, RunStep runStep);
}
