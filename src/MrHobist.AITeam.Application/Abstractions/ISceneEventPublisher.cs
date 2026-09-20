namespace MrHobist.AITeam.Application.Abstractions;

/// <summary>
/// Canli sahneye olay yayimlar. Sahne kozmetiktir: yayin kalici degildir, abone yoksa olay dusur.
/// Bugun <c>POST /api/v1/scene/commands</c> yazar; Faz 4-5'te <c>RunService</c> ayni kapidan yazar.
/// Uygulamasi host tarafindadir (Api: bellek ici SSE kanali).
/// </summary>
public interface ISceneEventPublisher
{
    /// <param name="type"><see cref="SceneEventTypes.All"/> icinden bir tur.</param>
    /// <param name="json">Olayin <c>data</c> govdesi; UI'a oldugu gibi gider.</param>
    void Publish(string type, string json);
}

/// <summary>UI'in tanidigi olay turleri (docs/SCENE.md). Yeni tur sona eklenir, var olan silinmez (CLAUDE.md §5).</summary>
public static class SceneEventTypes
{
    public const string AgentState = "agent.state";   // { agent, state: idle|working|thinking|blocked|waiting|done, note? }
    public const string AgentSay = "agent.say";       // { agent, kind: talk|ask|alert, text?, ms? }
    public const string AgentGoto = "agent.goto";     // { agent, spot }  -> spots[] icinden
    public const string AgentHome = "agent.home";     // { agent }
    public const string Meet = "meet";                // { from, to, kind: handoff|ask|reject, ms? }
    public const string BoardSet = "board.set";       // { tasks: [{ id, title, stage, state: queued|active|blocked|done }] }
    public const string BoardMove = "board.move";     // { task, stage, state }
    public const string RunStage = "run.stage";       // { stage, task, round }
    public const string Cat = "cat";                  // { action: sleep|wander|sit, spot? }
    public const string Door = "door";                // { state: closed|open }  iki kare; acilan kapi 1.4 s sonra kapanir
    public const string AgentLeave = "agent.leave";   // { agent }  kapiya yurur, disari cikar
    public const string AgentEnter = "agent.enter";   // { agent }  kapidan girer, evine yurur
    public const string ClockSet = "clock.set";       // { hour: 0-24 | null }  null = gercek saat
    public const string CafeSpecial = "cafe.special"; // { text: string | null }  null = liste doner
    public const string WorkflowSet = "workflow.set";  // { key }  pano sutunlari o is akisina gore kurulur
    public const string Light = "light";              // { id, state: on|off }  tiklanabilir isik (mudur odasi sarkiti); mutlak durum
    public const string SceneReload = "scene.reload";  // { reason }  config/scene.json degisti (ajan eklendi/silindi, masa eklendi): UI sahneyi yeniden kurar

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        AgentState, AgentSay, AgentGoto, AgentHome, Meet, BoardSet, BoardMove, RunStage,
        Cat, Door, AgentLeave, AgentEnter, ClockSet, CafeSpecial, WorkflowSet, Light, SceneReload,
    };
}
