// <copyright file="LocalizedStringSafeValidator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework.Extensions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;

/// <summary>
/// Patches <see cref="NpgsqlModelValidator"/> so it stops rejecting
/// <c>LocalizedString</c> properties. EF Core 10 + Npgsql 10.0.0
/// run a property-mapping check (<c>FindTypeMapping() == null</c>) BEFORE
/// consulting the registered <c>ValueConverter</c> set in
/// <c>OnModelCreating</c>; the converter-backed mapping is invisible to it
/// and the model fails to build with
/// <c>"The property 'X' could not be mapped because it is of type 'LocalizedString'"</c>.
/// The previous workaround on the fork (NF Core 9) was that the relational
/// validator consulted the converter. That changed in EF Core 10.
///
/// We override the public <c>Validate</c> entry point, delegate to the base
/// implementation, and catch ONLY the specific
/// <see cref="InvalidOperationException"/> raised against <c>LocalizedString</c>.
/// Any other mapping or Npgsql-level error still propagates.
///
/// The check is message-based, not type-based, so it survives Npgsql
/// releasing a fix: once the validator stops raising this specific message,
/// the catch never matches and behavior is identical to the unpatched base.
/// Drop this file (and the <c>ReplaceService</c> in
/// <see cref="ConnectionConfigurator"/>) once the upstream library is fixed.
/// </summary>
public sealed class LocalizedStringSafeValidator : NpgsqlModelValidator
{
    /// <summary>
    /// Substring from <c>CoreStrings.PropertyNotAdded</c>: matches any
    /// "could not be mapped" diagnostic. The localizedstring fragment is
    /// the C# simple name of <c>MUnique.OpenMU.Interfaces.LocalizedString</c>.
    /// </summary>
    private const string LocalizedStringTypeToken = "LocalizedString";

    private const string CouldNotBeMappedToken = "could not be mapped";

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalizedStringSafeValidator"/> class.
    /// Constructor signature matches the Npgsql base class so it can replace it
    /// in the DI container via <c>ReplaceService&lt;IModelValidator, LocalizedStringSafeValidator&gt;</c>.
    /// </summary>
    /// <param name="dependencies">EF Core model validator dependencies.</param>
    /// <param name="relationalDependencies">EF Core relational validator dependencies.</param>
    /// <param name="npgsqlSingletonOptions">The active Npgsql singleton options.</param>
    public LocalizedStringSafeValidator(
        ModelValidatorDependencies dependencies,
        RelationalModelValidatorDependencies relationalDependencies,
        INpgsqlSingletonOptions npgsqlSingletonOptions)
        : base(dependencies, relationalDependencies, npgsqlSingletonOptions)
    {
    }

    /// <inheritdoc />
    public override void Validate(IModel model, IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        try
        {
            base.Validate(model, logger);
        }
        catch (InvalidOperationException ex)
            when (IsLocalizedStringMappingError(ex))
        {
            // Intentionally swallowed. The property is correctly mapped via
            // a ValueConverter registered in EntityDataContext.OnModelCreating;
            // the validator just doesn't see the converter-backed mapping yet.
        }
    }

    /// <summary>
    /// Matches the specific EF Core diagnostic raised when a property
    /// type has no native type mapping AND no value-converter-backed
    /// mapping that the relational validator can see. We only suppress
    /// this when the offending type is <c>LocalizedString</c> — every
    /// other mapping error still surfaces.
    /// </summary>
    private static bool IsLocalizedStringMappingError(InvalidOperationException ex)
    {
        var message = ex.Message;
        return message.Contains(LocalizedStringTypeToken, StringComparison.Ordinal)
            && message.Contains(CouldNotBeMappedToken, StringComparison.Ordinal);
    }
}
