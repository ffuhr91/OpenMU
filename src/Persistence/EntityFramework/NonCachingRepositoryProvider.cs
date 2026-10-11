// <copyright file="NonCachingRepositoryProvider.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// A repository provider which does only provide non-caching repositories.
/// </summary>
internal class NonCachingRepositoryProvider : RepositoryProvider
{
    private readonly IContextAwareRepositoryProvider? _parent;

    /// <summary>
    /// Initializes a new instance of the <see cref="NonCachingRepositoryProvider"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="parent">The parent.</param>
    /// <param name="changeListener">The change publisher.</param>
    /// <param name="contextStack">The context stack.</param>
    public NonCachingRepositoryProvider(ILoggerFactory loggerFactory, IContextAwareRepositoryProvider? parent, IConfigurationChangeListener? changeListener, IContextStack contextStack)
        : base(loggerFactory, changeListener, contextStack)
    {
        this._parent = parent;
    }

    /// <summary>
    /// Registers the repositories.
    /// </summary>
    protected override void Initialize()
    {
        this.RegisterRepository(new GameConfigurationRepository(this._parent ?? this, this.LoggerFactory, this.ChangeListener));

        this.RegisterRepository(new AccountRepository(this._parent ?? this, this.LoggerFactory)); // never cache accounts, so we pass this
        this.RegisterRepository(new LetterBodyRepository(this._parent ?? this, this.LoggerFactory)); // never cache letters, so we pass this

        this.RegisterMissingRepositoriesAsGeneric();
        base.Initialize();
    }

    /// <summary>
    /// Registers generic repositories for all types of the data model which are not registered otherwise.
    /// </summary>
    protected void RegisterMissingRepositoriesAsGeneric()
    {
        using var entityContext = new EntityDataContext();
        var modelTypes = entityContext.Model.GetEntityTypes().Select(e => e.ClrType);

        // The original code snapshotted <c>Repositories.Keys</c> once and then
        // filtered against that snapshot. That fails for two reasons:
        //   1. Aggregate roots with dedicated repositories
        //      (<c>Account</c>, <c>GameConfiguration</c>, <c>LetterBody</c>) extend
        //      <see cref="object"/> directly — the snapshot's
        //      <c>!registeredTypes.Contains(t.BaseType)</c> check returns false for
        //      <c>object</c>, so the loop tried to register a second
        //      <c>GenericRepository&lt;T&gt;</c> on top of the dedicated one and
        //      threw <c>ArgumentException: An item with the same key has already
        //      been added</c>.
        //   2. The snapshot is taken BEFORE the loop modifies <c>Repositories</c>.
        //      When a type with a non-object <c>BaseType</c> enters the loop,
        //      <see cref="RepositoryProvider.RegisterRepository"/> stores the
        //      repository under <c>t.BaseType</c>. The next sibling type that
        //      shares the same <c>BaseType</c> doesn't see that update in the
        //      snapshot and tries to register the same key again.
        //
        // Fix: iterate every model type and check the LIVE <c>Repositories</c>
        // dictionary each iteration. Performance is O(n) per check, so the
        // total is O(n²) in the number of model types — acceptable for OpenMU's
        // ~60-entity model and avoids the stale-snapshot class of bugs entirely.
        foreach (var type in modelTypes)
        {
            if (type.BaseType is null)
            {
                continue;
            }

            if (this.Repositories.ContainsKey(type) || this.Repositories.ContainsKey(type.BaseType))
            {
                continue;
            }

            var repository = this.CreateGenericRepository(type, this._parent ?? this);
            this.RegisterRepository(type, repository);
        }
    }
}