using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RagFilingExplorer.Local.Tests")]
[assembly: InternalsVisibleTo("LinearizeSpike")] // tools/LinearizeSpike - the linearization spike runner

// Moq mocks VectorStoreCollection<int, FilingChunkRecord> via Castle DynamicProxy, which generates the
// mock into its own dynamic assembly ("DynamicProxyGenAssembly2"). Because FilingChunkRecord is an
// internal type used as a generic argument there, Castle needs this assembly's permission too - and
// because Microsoft.Extensions.VectorData.Abstractions is strong-named, that permission must be scoped
// to Castle's well-known public key, not just the assembly name. This is Castle/Moq's own documented
// workaround for mocking a type that exposes an internal generic argument.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2, PublicKey=0024000004800000940000000602000000240000525341310004000001000100c547cac37abd99c8db225ef2f6c8a3602f3b3606cc9891605d02baa56104f4cfc0734aa39b93bf7852f7d9266654753cc297e7d2edfe0bac1cdcf9f717241550e0a7b191195b7667bb4f64bcb8e2121380fd1d9d46ad2d92d2d15605093924cceaf74c4861eff62abf69b9291ed0a340e113be11e6a7d3113e92484cf7045cc7")]
