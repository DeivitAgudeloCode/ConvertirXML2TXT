//Conversor XML 2.txt (lectura robusta por Regex)
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// reglas 
public sealed record FieldRule(
    string Tag,
    string? Label,
    string? Format,
    string? DefaultValue
);

public sealed record MappingRules(
    string RowTag,
    string OutputMode,
    string OutputSeparator,
    bool PrintLabels,
    List<FieldRule> Fields
);

// ransformación 
static class Transformacion
{
    public static string Apply(string value, string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return value;

        var parts = format.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = value;

        foreach (var p in parts)
        {
            if (p.Equals("trim", StringComparison.OrdinalIgnoreCase))
                result = result.Trim();
            else if (p.Equals("upper", StringComparison.OrdinalIgnoreCase))
                result = result.ToUpperInvariant();
            else if (p.Equals("lower", StringComparison.OrdinalIgnoreCase))
                result = result.ToLowerInvariant();
            else if (p.StartsWith("date:", StringComparison.OrdinalIgnoreCase))
            {
                var spec = p["date:".Length..];
                var arrow = spec.IndexOf("->", StringComparison.Ordinal);
                if (arrow > 0)
                {
                    var fromFmt = spec[..arrow];
                    var toFmt = spec[(arrow + 2)..];
                    if (DateTime.TryParseExact(result, fromFmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        result = dt.ToString(toFmt, CultureInfo.InvariantCulture);
                }
            }
            else if (p.StartsWith("padleft:", StringComparison.OrdinalIgnoreCase))
            {
                // padleft:10,0
                var arg = p["padleft:".Length..];
                var tokens = arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                int width = int.Parse(tokens[0]);
                char pad = tokens.Length > 1 ? tokens[1][0] : ' ';
                result = result.PadLeft(width, pad);
            }
            else if (p.StartsWith("padright:", StringComparison.OrdinalIgnoreCase))
            {
                // padright:10,0
                var arg = p["padright:".Length..];
                var tokens = arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                int width = int.Parse(tokens[0]);
                char pad = tokens.Length > 1 ? tokens[1][0] : ' ';
                result = result.PadRight(width, pad);
            }
        }

        return result;
    }
}

// Programa
public class Program
{
    public static int Main(string[] args)
    {
        // Uso deL dotnet run -- -i input.xml -o salida.txt -m reglas.mapeo.json
        var inputPath = GetArg(args, "-i") ?? "input.xml";
        var outputPath = GetArg(args, "-o") ?? "salida.txt";
        var mapPath = GetArg(args, "-m") ?? "reglas.mapeo.json";

        Console.WriteLine($"[DEBUG] CurrentDirectory: {Directory.GetCurrentDirectory()}");

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"No existe el archivo XML: {inputPath}");
            return 1;
        }
        if (!File.Exists(mapPath))
        {
            Console.Error.WriteLine($"No existe el archivo de reglas: {mapPath}");
            return 1;
        }

        var mapping = JsonSerializer.Deserialize<MappingRules>(
            File.ReadAllText(mapPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        ) ?? throw new InvalidOperationException("reglas.mapeo.json inválido.");

        // extrae cada <ROW> con Regex 
        string raw = File.ReadAllText(inputPath);

        // QuitaR declaraciones XML repetidas
        raw = Regex.Replace(raw, @"<\?xml[^?]*\?>", "", RegexOptions.IgnoreCase | RegexOptions.Multiline).Trim();

        // capturar TODAS las etiquetas <ROW>...</ROW>
        string tag = mapping.RowTag; 
        string pattern = $@"<(?<ns>[\w\-]+:)?{Regex.Escape(tag)}\b[\s\S]*?</\k<ns>?{Regex.Escape(tag)}>";
        var rowMatches = Regex.Matches(raw, pattern, RegexOptions.IgnoreCase);

        if (rowMatches.Count == 0)
        {
            Console.Error.WriteLine($"No se encontraron nodos <{tag}> con Regex.");
            return 1;
        }

        var sb = new StringBuilder();
        var nbPersonal = GetArg(args, "-n") ?? "sevenTodo"; // valor predefinido por el sistema 
        sb.AppendLine($"tw.local.{nbPersonal}] = new tw.object. {nbPersonal} (););");

        int idx = 0;
        foreach (Match m in rowMatches)
        {
            // Parsear SOLO el fragmento del ROW como XElement
            XElement row;
            try
            {
                row = XElement.Parse(m.Value, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] No se pudo parsear un ROW #{idx}: {ex.Message}");
                continue;
            }

            sb.Append(BuildSevenTodoBlock(row, mapping, idx, nbPersonal));
            idx++;
        }

        File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"OK. Generado: {Path.GetFullPath(outputPath)} (filas: {idx})");
        return 0;
    }

    // Convertidores a "tw.local.sevenTodo[...]"
    static string BuildSevenTodoBlock(XElement row, MappingRules rules, int i, string nbPersonal)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\t\ttw.local. {nbPersonal} [ {i} ] = new tw.object.{nbPersonal}");

        // FECHA tratada especial
        var fechaRaw = GetValue(row, "FECHA");
        if (!string.IsNullOrWhiteSpace(fechaRaw))
        {
            // Normaliza separadores; si tu fuente es dd/MM/yyyy, ajusta el parse según corresponda.
            var fechaTmp = fechaRaw.Replace("-", "/");
            sb.AppendLine($"\t\tvar fechaTmp = '{Escape(fechaTmp)}';");
            sb.AppendLine($"\t\ttw.local.{nbPersonal}[{i}].FECHA = new TWDate();");
            sb.AppendLine($"\t\ttw.local.{nbPersonal}[{i}].FECHA.parse(fechaTmp,\"yyyy/MM/dd\");");
        }

        // Imprime los campos en el orden definido por rules.Fields (FECHA ya se imprimió)
        foreach (var f in rules.Fields)
        {
            var tag = f.Tag;
            if (tag.Equals("FECHA", StringComparison.OrdinalIgnoreCase)) continue;

            var val = GetValue(row, tag);
            val = Transformacion.Apply(val, f.Format);
            val ??= f.DefaultValue ?? "";

            if (tag.Equals("DEBITO", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("CREDITO", StringComparison.OrdinalIgnoreCase))
            {
                // Si parece número, sin comillas; si no, como string
                if (decimal.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    sb.AppendLine($"\t\ttw.local.{nbPersonal}[{i}].{tag} = {val};");
                else
                    sb.AppendLine($"\t\ttw.local.{nbPersonal}[{i}].{tag} = '{Escape(val)}';");
            }
            else
            {
                sb.AppendLine($"\t\ttw.local.{nbPersonal}[{i}].{tag} = '{Escape(val)}';");
            }
        }

        return sb.ToString();
    }

    // Leee el elemento hijo ignorando namespace y mayúsculas
    static string GetValue(XElement row, string tag)
    {
        var el = row.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, tag, StringComparison.OrdinalIgnoreCase));
        return el?.Value?.Trim() ?? "";
    }

    // Escapa comillas simples y backslash para que sea pegable
    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}