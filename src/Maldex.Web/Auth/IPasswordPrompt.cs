using System.Text;

namespace Maldex.Web.Auth;

/// <summary>Reads a password from a person at a terminal (the admin CLI's prompt; a fake in tests).</summary>
public interface IPasswordPrompt
{
    /// <summary>Shows <paramref name="prompt"/> and returns what was typed, without echo.</summary>
    /// <exception cref="InvalidOperationException">There is no terminal to read from.</exception>
    string ReadPassword(string prompt);
}

/// <summary>Reads from the console with <c>Console.ReadKey(intercept: true)</c>, so nothing is echoed. The prompt goes to stderr.</summary>
public sealed class ConsolePasswordPrompt : IPasswordPrompt
{
    public string ReadPassword(string prompt)
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("no terminal to prompt on; pass --password-stdin and the password on standard input");

        Console.Error.Write(prompt);
        var typed = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0) typed.Length--;
            }
            else if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                Console.Error.WriteLine();
                throw new OperationCanceledException();
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
        Console.Error.WriteLine();
        return typed.ToString();
    }
}
