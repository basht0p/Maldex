using System.Reflection;
using Microsoft.Data.SqlClient;

namespace Maldex.Integration.Tests.Fixtures;

/// <summary>Builds a <see cref="SqlException"/> with a given error number (SqlClient has no public constructor for it).</summary>
public static class SqlErrors
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    public static SqlException Exception(int number, string message = "simulated SQL error")
    {
        var errorCtor = typeof(SqlError).GetConstructors(Any).OrderByDescending(c => c.GetParameters().Length).First();
        var args = errorCtor.GetParameters().Select(p => p.Name switch
        {
            "infoNumber" => (object?)number,
            "errorState" or "errorClass" => (byte)0,
            "server" => "test",
            "errorMessage" => message,
            "procedure" => "",
            "lineNumber" => 0,
            "win32ErrorCode" => Convert.ChangeType(0, p.ParameterType, System.Globalization.CultureInfo.InvariantCulture),
            "exception" => null,
            "batchIndex" => -1,
            _ => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null,
        }).ToArray();
        var error = (SqlError)errorCtor.Invoke(args);

        var collection = (SqlErrorCollection)typeof(SqlErrorCollection).GetConstructors(Any).First(c => c.GetParameters().Length == 0).Invoke(null);
        typeof(SqlErrorCollection).GetMethod("Add", Any)!.Invoke(collection, [error]);
        var create = typeof(SqlException).GetMethods(Any)
            .Where(m => m.Name == "CreateException" && m.GetParameters()[0].ParameterType == typeof(SqlErrorCollection))
            .OrderBy(m => m.GetParameters().Length)
            .First();
        var createArgs = create.GetParameters()
            .Select((p, i) => i == 0 ? collection : p.ParameterType == typeof(string) ? "16.0" : p.HasDefaultValue ? p.DefaultValue : null)
            .ToArray();
        return (SqlException)create.Invoke(null, createArgs)!;
    }
}
