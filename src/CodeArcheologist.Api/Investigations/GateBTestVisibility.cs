using System.Runtime.CompilerServices;

// Only the explicitly trusted local adapter-contract executable may inject
// a loopback provider endpoint for bounded transport-failure verification.
[assembly: InternalsVisibleTo("MafAdapterContractProbe")]

// The trusted evaluation lab reuses the exact snapshot fingerprint function
// (never a re-implementation) to check pinned curated per-file manifests.
[assembly: InternalsVisibleTo("EvaluationLab")]
