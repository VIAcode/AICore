using Microsoft.KernelMemory.AI;

namespace AiCoreApi.Common.KernelMemory
{
    public sealed class EmptyTextGenerator : ITextGenerator
    {
        public EmptyTextGenerator()
        {
        }

        public int CountTokens(string text)
        {
            return 0;
        }

        public IReadOnlyList<string> GetTokens(string text)
        {
            return new List<string>();
        }

        public IAsyncEnumerable<string> GenerateTextAsync(string prompt, TextGenerationOptions options, CancellationToken cancellationToken = new CancellationToken())
        {
            return new List<string>().ToAsyncEnumerable();
        }

        public int MaxTokenTotal { get; } = 0;
    }
}
