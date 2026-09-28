using MrHobist.AITeam.Application.Runs;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// Kodla imza cikarimi (gorev basinda "bu iste yazilan kod"). Olcut derleyici dogrulugu degil: sonraki gorev dosyayi
/// ACMADAN "burada ne var" sorusunu cevaplayabilmeli -- tur, genel uye, HTTP ucu, disa aktarilan; govde ve gizli uye yok.
/// </summary>
public sealed class CodeDigestTests
{
    [Fact]
    public void CSharp_tur_genel_uye_ve_uclar_cikar_govde_ve_gizli_uye_cikmaz()
    {
        const string code = """
            namespace Api;

            /// public class YorumdakiSinif
            public sealed record KitapDto(int Id, string Baslik);

            internal static class KitapEndpoints
            {
                public static RouteGroupBuilder MapKitap(this RouteGroupBuilder g)
                {
                    g.MapGet("/kitaplar/{id}", GetAsync);
                    g.MapPost("/kitaplar", CreateAsync);
                    return g;
                }

                public string Ad { get; init; } = "";

                public const int Tavan = 5;

                private static Task<KitapDto> Gizli() => null!;

                private sealed class IcSinif;
            }

            [Route("api/raporlar")]
            public class RaporController : ControllerBase
            {
                [HttpGet("{id}")]
                public IActionResult Get(int id) => Ok();
            }
            """;

        Assert.Equal(
            [
                "public sealed record KitapDto(int Id, string Baslik);",
                "internal static class KitapEndpoints",
                "public static RouteGroupBuilder MapKitap(this RouteGroupBuilder g)",
                "g.MapGet(\"/kitaplar/{id}\")",
                "g.MapPost(\"/kitaplar\")",
                "public string Ad",
                "[Route(\"api/raporlar\")]",
                "public class RaporController : ControllerBase",
                "[HttpGet(\"{id}\")]",
                "public IActionResult Get(int id)",
            ],
            CodeDigest.Signatures("src/Api/KitapEndpoints.cs", code));
    }

    [Fact]
    public void TypeScript_ve_Vue_disa_aktarilanlar_ve_makrolar_cikar_yarim_bildirim_bolunmez()
    {
        const string ts = """
            import { ref } from 'vue'

            export interface Satir { id: number }
            export type Filtre = { q: string }
            export const API = '/api/v1'
            export const useListe = (f: Filtre): Satir[] => {
              return []
            }
            export default defineEventHandler(async (event) => {
            })
            function yerel() {}
            """;
        Assert.Equal(
            [
                "export interface Satir",
                "export type Filtre = { q: string }",
                "export const API = '/api/v1'",
                "export const useListe = (f: Filtre): Satir[]",
                "export default defineEventHandler(async (event) => {",
            ],
            CodeDigest.Signatures("server/api/liste.ts", ts));

        const string vue = """
            <script setup lang="ts">
            const props = defineProps<{ kitapId: number }>()
            const emit = defineEmits<{ (e: 'kaydet', id: number): void }>()
            </script>
            <template><div>{{ props.kitapId }}</div></template>
            """;
        Assert.Equal(
            ["const props = defineProps<{ kitapId: number }>()", "const emit = defineEmits<{ (e: 'kaydet', id: number): void }>()"],
            CodeDigest.Signatures("pages/kitap/[id].vue", vue));
    }

    [Fact]
    public void Python_ust_duzey_genel_metot_ve_rota_cikar_gizli_ve_ic_fonksiyon_cikmaz()
    {
        const string py = """
            @router.post("/complete")
            async def complete(request: CompleteRequest) -> CompleteResponse:
                def ic():
                    pass

            class Saglayici:
                def tamamla(self, istek):
                    pass

                def _gizli(self):
                    pass
            """;
        Assert.Equal(
            ["@router.post(\"/complete\")", "async def complete(request: CompleteRequest) -> CompleteResponse", "class Saglayici", "def tamamla(self, istek)"],
            CodeDigest.Signatures("app/main.py", py));
    }

    [Fact]
    public void Dosya_basina_imza_tavani_ve_uzun_satir_kesilir_imzasiz_uzantida_bos()
    {
        var many = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"export const sabit{i} = {i}"));
        Assert.Equal(CodeDigest.MaxSignaturesPerFile, CodeDigest.Signatures("a.ts", many).Count);

        var longSig = CodeDigest.Signatures("a.cs", "public void Uzun(" + new string('x', 300) + ")");
        Assert.Equal(160, Assert.Single(longSig).Length);
        Assert.EndsWith("…", longSig[0], StringComparison.Ordinal);

        Assert.Empty(CodeDigest.Signatures("appsettings.json", "{ \"public class\": 1 }"));
        Assert.False(CodeDigest.HasSignatures("appsettings.json"));
        Assert.True(CodeDigest.HasSignatures("Pages/Index.VUE"));
    }
}
