using System.Text;

namespace Maldex.Web.Auth;

/// <summary>Reads a password from a person at a terminal (the admin CLI's prompt; a fake in tests).</summary>
public interface IPasswordPrompt
{
    /// <summary>Shows <paramref name="prompt"/> and returns what was typed, without echo.</summary>
    /// <exception cref="NoTerminalException">There is no terminal to read from.</exception>
    /// <exception cref="OperationCanceledException">The person pressed Ctrl+C.</exception>
    string ReadPassword(string prompt);
}

/// <summary>There is no terminal to prompt on (standard input is redirected): the caller should pass <c>--password-stdin</c>.</summary>
public sealed class NoTerminalException() : Exception("no terminal to prompt on; pass --password-stdin and the password on standard input");

/// <summary>Reads from the console with <c>Console.ReadKey(intercept: true)</c>, so nothing is echoed. The prompt goes to stderr.</summary>
public sealed class ConsolePasswordPrompt : IPasswordPrompt
{
    public string ReadPassword(string prompt)
    {
        if (Console.IsInputRedirected) throw new NoTerminalException();

        // Ctrl+C must reach ReadKey as a key: as a signal it only cancels a token that ReadKey does not watch, and the
        // process's CancelKeyPress handler keeps the process alive, so the prompt would hang until Enter.
        var treatControlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
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
                else if ((key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) || key.KeyChar == '\u0003')
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
        finally
        {
            Console.TreatControlCAsInput = treatControlC;
        }
    }
}
