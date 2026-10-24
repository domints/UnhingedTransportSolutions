using System.Data;
using Dapper;

namespace Uts.Api.Db;

/// <summary>Teaches Dapper how to materialize Postgres text[] columns into
/// string[]. Without this, the generated IL deserializer throws "a
/// parameterless default constructor or one matching signature … is
/// required" on any record carrying an array column.</summary>
public sealed class StringArrayTypeHandler : SqlMapper.TypeHandler<string[]>
{
    public override string[] Parse(object value) => value as string[] ?? [];

    public override void SetValue(IDbDataParameter parameter, string[]? value) =>
        parameter.Value = value;
}
