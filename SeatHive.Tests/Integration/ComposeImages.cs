using System.Text.RegularExpressions;

namespace SeatHive.Tests.Integration
{
    // The images the tests start are the ones docker-compose.yml runs, read from that file, so the tests always
    // check the versions a server would run and the two cannot drift apart.
    public static partial class ComposeImages
    {
        private const string FileName = "docker-compose.yml";

        // "image: redis:8.10-alpine" for the image named "redis".
        [GeneratedRegex(@"^\s*image:\s*(?<image>(?<name>[a-z0-9._/-]+):[A-Za-z0-9._-]+)\s*$", RegexOptions.Multiline)]
        private static partial Regex ImageLine();

        private static readonly Lazy<string> ComposeFile = new(FindComposeFile);
        private static readonly Lazy<string> Compose = new(() => File.ReadAllText(ComposeFile.Value));

        // The repository root: where docker-compose.yml is.
        public static string RepositoryRoot => Path.GetDirectoryName(ComposeFile.Value)!;

        public static string Of(string name)
        {
            var image = ImageLine().Matches(Compose.Value)
                .Select(m => m.Groups)
                .FirstOrDefault(g => g["name"].Value == name)?["image"].Value;

            return image ?? throw new InvalidOperationException(
                $"{FileName} has no 'image: {name}:<tag>' line; the tests take their {name} image from there.");
        }

        // The test binaries sit somewhere below the repository root.
        private static string FindComposeFile()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, FileName);
                if (File.Exists(path)) return path;
            }

            throw new InvalidOperationException($"{FileName} was not found above {AppContext.BaseDirectory}.");
        }
    }
}
