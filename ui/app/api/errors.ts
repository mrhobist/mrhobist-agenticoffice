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
  'agent.invalid_provider': 'Sağlayıcı geçersiz: anthropic, openai, nvidia ya da ollama olmalı.',
  'agent.unknown_include': 'Seçilen bilgi dosyalarından biri config/knowledge içinde yok.',
  'agent.unknown_can_ask': '"Sorabilir" alanındaki ajan tanımlı değil.',
  'agent.invalid_effort': 'Efor geçersiz: low, medium, high ya da max olmalı.',
  'agent.exists': 'Bu anahtarla bir ajan zaten var.',
  'agent.in_use': 'Ajan bir iş akışında ya da başka bir ajanın "Sorabilir" alanında kullanılıyor; önce oradan çıkarın.',
  'agent.unknown_sprite': 'Seçilen karakter sahnenin karakter listesinde yok.',
  'agent.unknown_mcp': 'Seçilen MCP sunucularından biri kayıtlı değil.',
  'agent.mcp_unsupported': 'Bu ajanın sağlayıcısı MCP araçlarını çalıştıramıyor; MCP yetkisi yalnız Anthropic (Claude) ajanlarına verilebilir.',

  'mcp.invalid_key': 'MCP anahtarı geçersiz: yalnız küçük harf, rakam, tire ve alt çizgi kullanılabilir.',
  'mcp.invalid': 'MCP tanımı eksik: stdio için komut, http/sse için http(s) adresi gerekli; değişken/başlık adları boş olamaz.',
  'mcp.not_found': 'MCP sunucusu bulunamadı.',
  'mcp.exists': 'Bu anahtarla bir MCP sunucusu zaten var.',
  'mcp.oauth_unsupported': 'OAuth yalnız uzak (HTTP/SSE) MCP sunucularında kullanılır.',
  'mcp.oauth_discovery_failed': 'Sunucunun yetki (OAuth) bilgisi bulunamadı ya da güvenli değil; sunucu OAuth desteklemiyor olabilir.',
  'mcp.oauth_client_required': 'Bu sunucu otomatik istemci kaydı yapmıyor: kendi OAuth uygulamanın istemci kimliğini (ve gizli anahtarını) gir.',
  'mcp.oauth_state_invalid': 'Giriş oturumunun süresi doldu; girişi panelden yeniden başlat.',
  'mcp.oauth_token_failed': 'Yetkilendirme reddedildi ya da belirteç alınamadı.',
  'mcp.catalog_not_found': 'Hazır sunucu ya da bağlantı yöntemi katalogda bulunamadı.',
  'mcp.in_use': 'Bu MCP sunucusu ajanlara yetkili; silmeden önce yetkileri kaldırın.',

  'attachment.empty': 'Dosya boş.',
  'attachment.too_large': 'Dosya çok büyük (üst sınır 20 MB).',
  'attachment.type_unsupported': 'Bu dosya türü eklenemez. PDF, resim (png, jpg, gif, webp), Word (docx) ve metin dosyaları desteklenir.',
  'attachment.too_many': 'Bir işe en fazla 10 dosya eklenebilir.',
  'attachment.not_found': 'Ek bulunamadı ya da süresi doldu; dosyayı yeniden ekleyin.',

  'team.missing_role': 'İş akışının gerektirdiği bir rol ekipte eksik.',

  'knowledge.invalid_key': 'Bilgi dosyası anahtarı geçersiz.',
  'knowledge.not_found': 'Bilgi dosyası bulunamadı.',
  'knowledge.in_use': 'Bilgi dosyası bir ajanın listesinde; önce oradan çıkarın.',
  'knowledge.body_empty': 'Bilgi dosyasının gövdesi boş olamaz.',
  'agent.markdown_invalid': 'Ajan md dosyası çözülemedi: frontmatter (---) ve gövde biçimini kontrol edin.',

  'workflow.analyze_count': 'İş akışında tam olarak bir analiz adımı olmalı.',
  'workflow.analyze_first': 'Analiz adımı ilk sırada olmalı.',
  'workflow.no_implement': 'İş akışında en az bir geliştirme adımı olmalı.',
  'workflow.review_before_implement': 'İnceleme adımından önce bir geliştirme adımı gelmeli.',
  'workflow.rounds_min': 'En fazla inceleme turu en az 1 olmalı.',
  'workflow.duplicate_stage': 'Aynı adım kimliği birden çok kez kullanılmış.',
  'workflow.invalid_stage': 'Bir adımın türü, ofis rolü ya da ajanı tanımsız.',
  'workflow.unknown_role': 'Bir adımın ajanı ya da devir rolü ekipte tanımlı değil.',
  'workflow.not_found': 'İş akışı bulunamadı.',
  'workflow.default_protected': 'Varsayılan iş akışı silinemez.',

  'run.policy_violation': 'Çalışma politika kurallarına takıldı.',
  'run.budget_exceeded': 'Bütçe aşıldı; çalışma durduruldu.',
  'run.not_found': 'Çalışma bulunamadı.',
  'run.brief_empty': 'Brief boş olamaz.',
  'run.note_empty': 'Revize notu boş olamaz.',
  'run.not_awaiting_approval': 'Çalışma onay beklemiyor; bu işlem yalnız plan onay aşamasında yapılabilir.',
  'run.plan_invalid': 'Analistin planı şemaya uymadı; çalışma durdu.',
  'run.budget_invalid': 'Bütçe sıfırdan büyük olmalı ya da boş bırakılmalı.',
  'run.project_required': 'İş bir projenin içinde başlar; önce proje seç.',

  'run.not_awaiting_input': 'Çalışma senden cevap beklemiyor.',
  'run.invalid_choice': 'Seçenek geçersiz.',
  'run.step_invalid': 'Ajanın çıktısı beklenen biçimde değil; adım başarısız.',
  'settings.invalid': 'Ayar geçersiz: eşik 1–100 arasında olmalı.',
  'auth.required': 'Giriş gerekli.',
  'auth.invalid_credentials': 'Kullanıcı adı ya da şifre yanlış.',

  'project.invalid_key': 'Proje anahtarı geçersiz: yalnız küçük harf, rakam, tire ve alt çizgi.',
  'project.not_found': 'Proje bulunamadı.',
  'project.exists': 'Bu anahtarla bir proje zaten var.',
  'project.in_use': 'Projede süren çalışma var; önce bitirin ya da iptal edin.',
  'project.title_empty': 'Proje başlığı boş olamaz.',
  'project.target_dir_invalid': 'Hedef dizin depo içinde göreli bir yol olmalı.',
  'project.launch_missing': 'Proje kökünde run.cmd yok; developer henüz başlatıcı yazmadı.',
  'project.launch_failed': 'Başlatıcı çalıştırılamadı.',
  'project.invalid_color': 'Renk #rrggbb biçiminde olmalı.',
  'run.not_retryable': 'Çalışma yeniden denenemez; bu yalnız başarısız, yarıda kalmış, bütçesi aşılmış ya da iptal edilmiş çalışmalarda yapılabilir.',
  'run.not_cancellable': 'Çalışma iptal edilemez; yalnız çalışan, onay bekleyen ya da duraklamış çalışmalar iptal edilir.',

  'request.invalid': 'İstek çözümlenemedi: gövde ya da parametre beklenen biçimde değil.',

  'config.file_missing': 'Yapılandırma dosyası bulunamadı (config/).',
  'config.file_invalid': 'Yapılandırma dosyası okunamadı ya da biçimi bozuk.',
  'runtime.unavailable': 'Runtime (Python) kapalı; modele ulaşılamıyor.',
  'runtime.error': 'Runtime ayakta ama istenen uç hata verdi.',

  'scene.command.type_unknown': 'Sahne komutunun türü tanımsız.',
  'scene.command.type_missing': 'Sahne komutunda tür belirtilmemiş.',
  'scene.command.data_missing': 'Sahne komutunda veri nesnesi yok.',
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
