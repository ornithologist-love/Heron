namespace Heron.Pregel.Tests
{
    /// <summary>
    ///     Node IDs and graph fingerprints are persisted in checkpoints. A host that renames its namespace
    ///     registers the former name, and graphs built from the renamed types keep the identity saved runs use.
    /// </summary>
    [Anchor("Docs/pregel.md#身份与宿主边界")]
    public sealed class IdentityMappingTests
    {
        static IdentityMappingTests() =>
            PregelIdentity.MapName("Heron.Pregel.Tests.IdentityProbe.Renamed", "Heron.Pregel.Tests.IdentityProbe.Former");

        [Fact]
        public void RenamedTypes_MappedToTheirFormerName_KeepNodeIdsAndFingerprint()
        {
            var former = Build(new IdentityProbe.Former.Tasks().Step);
            var renamed = Build(new IdentityProbe.Renamed.Tasks().Step);

            renamed.Definition.Nodes.Keys.Should().BeEquivalentTo(former.Definition.Nodes.Keys);
            renamed.Definition.Fingerprint.Should().Be(former.Definition.Fingerprint);
        }

        [Fact]
        public void Mapping_StopsAtTheNamespaceBoundary()
        {
            var formerSibling = Build(new IdentityProbe.FormerSibling.Tasks().Step);
            var sibling = Build(new IdentityProbe.RenamedSibling.Tasks().Step);

            sibling.Definition.Fingerprint.Should().NotBe(formerSibling.Definition.Fingerprint,
                "a namespace that only shares a prefix with a mapped name keeps its own identity");
        }

        private static CompiledPregelGraph Build(Action<PregelUnit, PregelNodeContext, CancellationToken> step) =>
            Pregel.Define<PregelUnit>("identity-probe").Then(step).Compile().Compiled;
    }
}

namespace Heron.Pregel.Tests.IdentityProbe.Former
{
    public sealed class Tasks
    {
        public void Step(PregelUnit _, PregelNodeContext context, CancellationToken token) { }
    }
}

namespace Heron.Pregel.Tests.IdentityProbe.Renamed
{
    public sealed class Tasks
    {
        public void Step(PregelUnit _, PregelNodeContext context, CancellationToken token) { }
    }
}

namespace Heron.Pregel.Tests.IdentityProbe.RenamedSibling
{
    public sealed class Tasks
    {
        public void Step(PregelUnit _, PregelNodeContext context, CancellationToken token) { }
    }
}

namespace Heron.Pregel.Tests.IdentityProbe.FormerSibling
{
    public sealed class Tasks
    {
        public void Step(PregelUnit _, PregelNodeContext context, CancellationToken token) { }
    }
}
