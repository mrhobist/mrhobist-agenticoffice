using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.UnitTests;

/// <summary>Kopya kimlikleri (docs/DOMAIN.md → Kopyalar): kopya 1 anahtarin kendisi, sonrakiler anahtar~n; ~ anahtarda gecersiz.</summary>
public sealed class WorkersTests
{
    [Fact]
    public void Kopya_kimligi_ve_geri_cozum()
    {
        Assert.Equal("dev", Workers.Id("dev", 1));
        Assert.Equal("dev~3", Workers.Id("dev", 3));
        Assert.Equal(("dev", 3), (Workers.BaseOf("dev~3"), Workers.InstanceOf("dev~3")));
        Assert.Equal(("dev", 1), (Workers.BaseOf("dev"), Workers.InstanceOf("dev")));
        Assert.Equal(("a~b", 1), (Workers.BaseOf("a~b"), Workers.InstanceOf("a~b"))); // sayi degil: kopya degil
        Assert.Equal(["dev", "dev~2", "dev~3"], Workers.All("dev", 3));
        Assert.Equal(Workers.MaxInstances, Workers.All("dev", 99).Count());
    }

    [Fact]
    public void Kopya_ayiraci_ajan_anahtarinda_gecersiz()
        => Assert.Throws<DomainException>(() => Identifiers.Require("dev~2", ErrorCodes.AgentInvalidKey, "ajan"));

    [Fact]
    public void Max_instances_araligi_denetlenir()
    {
        static Agent A(int? n) => new("dev", "Dev", "", [], null, null, [], null, "govde", MaxInstances: n);
        Assert.Equal(1, A(null).Instances);
        Assert.Equal(5, A(5).Instances);
        A(8).Validate();
        Assert.Equal(ErrorCodes.AgentInvalidInstances, Assert.Throws<DomainException>(() => A(0).Validate()).ErrorCode);
        Assert.Equal(ErrorCodes.AgentInvalidInstances, Assert.Throws<DomainException>(() => A(9).Validate()).ErrorCode);
    }
}
