using System.Runtime.CompilerServices;

// Only the explicitly trusted local adapter-contract executable may inject
// a loopback provider endpoint for bounded transport-failure verification.
[assembly: InternalsVisibleTo("MafAdapterContractProbe")]
