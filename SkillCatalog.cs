using System.Text;

namespace CodexQuotaFloat;

internal sealed record CodexSkill(string Name, string Description, string Source, DateTime InstalledAt);

internal sealed record SkillCatalogSnapshot(DateTime RefreshedAt, IReadOnlyList<CodexSkill> Skills);

internal static class SkillCatalog
{
    public static SkillCatalogSnapshot Discover()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            (Path: Path.Combine(home, ".codex", "skills"), Source: "个人"),
            (Path: Path.Combine(home, ".agents", "skills"), Source: "个人"),
            (Path: Path.Combine(home, ".codex", "plugins", "cache"), Source: "插件")
        };

        var discovered = new List<CodexSkill>();
        foreach (var root in roots)
        {
            foreach (var skillFile in FindSkillFiles(root.Path))
            {
                var skill = TryReadSkill(skillFile, root.Source);
                if (skill is not null)
                {
                    discovered.Add(skill);
                }
            }
        }

        var skills = discovered
            .GroupBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(skill => SourcePriority(skill.Source))
                .ThenByDescending(skill => skill.InstalledAt)
                .ThenBy(skill => skill.Source, StringComparer.Ordinal)
                .First())
            .OrderByDescending(skill => skill.InstalledAt)
            .ThenBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SkillCatalogSnapshot(DateTime.Now, skills);
    }

    private static IEnumerable<string> FindSkillFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories).GetEnumerator();
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string? current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (IOException)
                {
                    yield break;
                }
                catch (UnauthorizedAccessException)
                {
                    yield break;
                }

                yield return current;
            }
        }
    }

    private static CodexSkill? TryReadSkill(string skillFile, string source)
    {
        try
        {
            using var reader = new StreamReader(skillFile, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var frontMatter = ReadFrontMatter(reader);
            var name = ReadYamlValue(frontMatter, "name")
                ?? Path.GetFileName(Path.GetDirectoryName(skillFile) ?? skillFile);
            var description = ReadYamlValue(frontMatter, "description") ?? "未提供说明";

            name = name.Trim();
            description = CollapseWhitespace(description);
            var installedAt = File.GetCreationTime(skillFile);
            if (installedAt <= DateTime.UnixEpoch)
            {
                installedAt = File.GetLastWriteTime(skillFile);
            }
            return string.IsNullOrWhiteSpace(name) ? null : new CodexSkill(name, description, source, installedAt);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadFrontMatter(StreamReader reader)
    {
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var lineCount = 0; lineCount < 80; lineCount++)
        {
            var line = reader.ReadLine();
            if (line is null || line.Trim() == "---")
            {
                break;
            }

            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static string? ReadYamlValue(string frontMatter, string key)
    {
        foreach (var line in frontMatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed[(key.Length + 1)..].Trim();
            return value.Trim('"', '\'');
        }

        return null;
    }

    private static string CollapseWhitespace(string value)
    {
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static int SourcePriority(string source) => source switch
    {
        "个人" => 2,
        "插件" => 1,
        _ => 0
    };
}
