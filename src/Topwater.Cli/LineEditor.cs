using System.Text;

namespace Topwater.Cli;

/// <summary>
/// Reads one line at a terminal with editing and history: arrows, Home/End, Ctrl+A/E/U/K/W, Ctrl+C clears the line,
/// Ctrl+D on an empty line ends input. .NET's Console.ReadLine has no history on Linux and macOS.
/// shortcut: redraws a single row, so a line wider than the terminal displays badly; use a readline library if that matters.
/// </summary>
sealed class LineEditor(List<string> history)
{
    public string? ReadLine(string prompt)
    {
        var line = new StringBuilder();
        int pos = 0, index = history.Count;
        string pending = "";   // what was typed before browsing history
        var wasTreated = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            Console.Write(prompt);
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                var ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Console.WriteLine();
                        var text = line.ToString();
                        if (text.Trim().Length > 0 && (history.Count == 0 || history[^1] != text)) history.Add(text);
                        return text;
                    case ConsoleKey.D when ctrl:
                        if (line.Length == 0) { Console.WriteLine(); return null; }
                        if (pos < line.Length) line.Remove(pos, 1);
                        break;
                    case ConsoleKey.C when ctrl:
                        Console.WriteLine("^C");
                        line.Clear(); pos = 0; index = history.Count;
                        Console.Write(prompt);
                        continue;
                    case ConsoleKey.Backspace:
                        if (pos > 0) line.Remove(--pos, 1);
                        break;
                    case ConsoleKey.Delete:
                        if (pos < line.Length) line.Remove(pos, 1);
                        break;
                    case ConsoleKey.LeftArrow or ConsoleKey.B when key.Key == ConsoleKey.LeftArrow || ctrl:
                        if (pos > 0) pos--;
                        break;
                    case ConsoleKey.RightArrow or ConsoleKey.F when key.Key == ConsoleKey.RightArrow || ctrl:
                        if (pos < line.Length) pos++;
                        break;
                    case ConsoleKey.Home or ConsoleKey.A when key.Key == ConsoleKey.Home || ctrl:
                        pos = 0;
                        break;
                    case ConsoleKey.End or ConsoleKey.E when key.Key == ConsoleKey.End || ctrl:
                        pos = line.Length;
                        break;
                    case ConsoleKey.U when ctrl:
                        line.Remove(0, pos); pos = 0;
                        break;
                    case ConsoleKey.K when ctrl:
                        line.Length = pos;
                        break;
                    case ConsoleKey.W when ctrl:
                        var start = pos;
                        while (start > 0 && line[start - 1] == ' ') start--;
                        while (start > 0 && line[start - 1] != ' ') start--;
                        line.Remove(start, pos - start); pos = start;
                        break;
                    case ConsoleKey.UpArrow or ConsoleKey.P when key.Key == ConsoleKey.UpArrow || ctrl:
                    case ConsoleKey.DownArrow or ConsoleKey.N when key.Key == ConsoleKey.DownArrow || ctrl:
                        var up = key.Key is ConsoleKey.UpArrow or ConsoleKey.P;
                        if (up ? index == 0 : index >= history.Count) break;
                        if (index == history.Count) pending = line.ToString();
                        index += up ? -1 : 1;
                        line.Clear().Append(index == history.Count ? pending : history[index]);
                        pos = line.Length;
                        break;
                    default:
                        if (key.KeyChar < ' ' || ctrl) continue;
                        line.Insert(pos++, key.KeyChar);
                        if (pos == line.Length) { Console.Write(key.KeyChar); continue; }   // typing at the end needs no redraw
                        break;
                }
                // redraw: carriage return, prompt and line, clear the rest, put the cursor back
                Console.Write($"\r{prompt}{line}\x1b[K");
                if (line.Length > pos) Console.Write($"\x1b[{line.Length - pos}D");
            }
        }
        finally { Console.TreatControlCAsInput = wasTreated; }
    }
}
