import { ApiError } from './client'

/**
 * errorCode → kullaniciya donuk Turkce metin. TEK esleme yeri (CLAUDE.md §5).
 * Kaynak: docs/API.md ve Domain/ErrorCodes.cs. Bilinmeyen kod genel metne duser;
 * Problem Details `title`/`detail` HICBIR ZAMAN ekrana basilmaz.
 */
const MESSAGES: Record<string, string> = {
  'agent.invalid_key': 'Ajan anahtarı geçersiz: yalnız küçük harf, rakam, tire ve alt çizgi kullanılabilir.',
  'agent.not_found': 'Ajan bulunamadı.',
  'agent.prompt_empty': 'Sistem promptu boş olamaz.',
  'agent.invalid_provider': 'Sağlayıcı geçersiz: anthropic, nvidia ya da ollama olmalı.',
  'agent.unknown_include': 'Seçilen bilgi dosyalarından biri config/knowledge içinde yok.',
  'agent.unknown_can_ask': '"Sorabilir" alanındaki ajan tanımlı değil.',
  'team.missing_role': 'İş akışının gerektirdiği bir rol ekipte eksik.',

  'knowledge.invalid_key': 'Bilgi dosyası anahtarı geçersiz.',

  'workflow.analyze_count': 'İş akışında tam olarak bir analiz adımı olmalı.',
  'workflow.analyze_first': 'Analiz adımı ilk sırada olmalı.',
  'workflow.no_implement': 'İş akışında en az bir geliştirme adımı olmalı.',
  'workflow.review_before_implement': 'İnceleme adımından önce bir geliştirme adımı gelmeli.',
  'workflow.rounds_min': 'En fazla inceleme turu en az 1 olmalı.',
  'workflow.duplicate_stage': 'Aynı adım kimliği birden çok kez kullanılmış.',
  'workflow.invalid_stage': 'Bir adımın türü, ofis rolü ya da ajanı tanımsız.',

  'run.policy_violation': 'Çalışma politika kurallarına takıldı.',
  'run.budget_exceeded': 'Bütçe aşıldı; çalışma durduruldu.',
  'run.not_found': 'Çalışma bulunamadı.',

  'request.invalid': 'İstek çözümlenemedi: gövde ya da parametre beklenen biçimde değil.',

  'config.file_missing': 'Yapılandırma dosyası bulunamadı (config/).',
  'config.file_invalid': 'Yapılandırma dosyası okunamadı ya da biçimi bozuk.',
  'runtime.unavailable': 'Runtime (Python) kapalı; modele ulaşılamıyor.',

  'scene.command.type_unknown': 'Sahne komutunun türü tanımsız.',
  'scene.command.body_invalid': 'Sahne komutunun gövdesi geçersiz.',
}

export function messageForCode(code: string): string {
  return MESSAGES[code] ?? `Beklenmeyen hata (${code}).`
}

/** Yakalanan herhangi bir hatayi ekrana basilacak metne cevirir. */
export function errorText(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.errorCode) return messageForCode(e.errorCode)
    if (e.status === 0) return "Api'ye ulaşılamadı."
    if (e.status === 400) return 'İstek geçersiz; sunucu gövdeyi kabul etmedi.'
    if (e.status === 404) return 'Uç bulunamadı.'
    return `Sunucu hatası (HTTP ${e.status}).`
  }
  return 'Beklenmeyen bir hata oluştu.'
}
