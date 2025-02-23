using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Navigation;
using Umbraco.Cms.Core.Persistence.Repositories;
using Umbraco.Cms.Core.Scoping;

namespace Umbraco.Cms.Core.Services.Navigation;

internal abstract class ContentNavigationServiceBase
{
    private readonly ICoreScopeProvider _coreScopeProvider;
    private readonly INavigationRepository _navigationRepository;
    private ConcurrentDictionary<Guid, NavigationNode> _navigationStructure = new();
    private ConcurrentDictionary<Guid, NavigationNode> _recycleBinNavigationStructure = new();
    private IList<Guid> _roots = new List<Guid>();
    private IList<Guid> _recycleBinRoots = new List<Guid>();

    protected ContentNavigationServiceBase(ICoreScopeProvider coreScopeProvider, INavigationRepository navigationRepository)
    {
        _coreScopeProvider = coreScopeProvider;
        _navigationRepository = navigationRepository;
    }

    /// <summary>
    ///     Rebuilds the entire main navigation structure. Implementations should define how the structure is rebuilt.
    /// </summary>
    public abstract Task RebuildAsync();

    /// <summary>
    ///     Rebuilds the recycle bin navigation structure. Implementations should define how the bin structure is rebuilt.
    /// </summary>
    public abstract Task RebuildBinAsync();

    public bool TryGetParentKey(Guid childKey, out Guid? parentKey)
        => TryGetParentKeyFromStructure(_navigationStructure, childKey, out parentKey);

    public bool TryGetRootKeys(out IEnumerable<Guid> rootKeys)
        => TryGetRootKeysFromStructure(_roots, out rootKeys);

    public bool TryGetChildrenKeys(Guid parentKey, out IEnumerable<Guid> childrenKeys)
        => TryGetChildrenKeysFromStructure(_navigationStructure, parentKey, out childrenKeys);

    public bool TryGetDescendantsKeys(Guid parentKey, out IEnumerable<Guid> descendantsKeys)
        => TryGetDescendantsKeysFromStructure(_navigationStructure, parentKey, out descendantsKeys);

    public bool TryGetAncestorsKeys(Guid childKey, out IEnumerable<Guid> ancestorsKeys)
        => TryGetAncestorsKeysFromStructure(_navigationStructure, childKey, out ancestorsKeys);

    public bool TryGetSiblingsKeys(Guid key, out IEnumerable<Guid> siblingsKeys)
        => TryGetSiblingsKeysFromStructure(_navigationStructure, key, out siblingsKeys);

    public bool TryGetParentKeyInBin(Guid childKey, out Guid? parentKey)
        => TryGetParentKeyFromStructure(_recycleBinNavigationStructure, childKey, out parentKey);

    public bool TryGetChildrenKeysInBin(Guid parentKey, out IEnumerable<Guid> childrenKeys)
        => TryGetChildrenKeysFromStructure(_recycleBinNavigationStructure, parentKey, out childrenKeys);

    public bool TryGetDescendantsKeysInBin(Guid parentKey, out IEnumerable<Guid> descendantsKeys)
        => TryGetDescendantsKeysFromStructure(_recycleBinNavigationStructure, parentKey, out descendantsKeys);

    public bool TryGetAncestorsKeysInBin(Guid childKey, out IEnumerable<Guid> ancestorsKeys)
        => TryGetAncestorsKeysFromStructure(_recycleBinNavigationStructure, childKey, out ancestorsKeys);

    public bool TryGetSiblingsKeysInBin(Guid key, out IEnumerable<Guid> siblingsKeys)
        => TryGetSiblingsKeysFromStructure(_recycleBinNavigationStructure, key, out siblingsKeys);

    public bool TryGetLevel(Guid contentKey, [NotNullWhen(true)] out int? level)
    {
        level = 1;
        if (TryGetParentKey(contentKey, out Guid? parentKey) is false)
        {
            level = null;
            return false;
        }

        while (parentKey is not null)
        {
            if (TryGetParentKey(parentKey.Value, out parentKey) is false)
            {
                level = null;
                return false;
            }

            level++;
        }

        return true;
    }

    public bool MoveToBin(Guid key)
    {
        if (TryRemoveNodeFromParentInStructure(_navigationStructure, key, out NavigationNode? nodeToRemove) is false || nodeToRemove is null)
        {
            return false; // Node doesn't exist
        }

        // Recursively remove all descendants and add them to recycle bin
        AddDescendantsToRecycleBinRecursively(nodeToRemove);

        // Reset the SortOrder based on its new position in the bin
        nodeToRemove.UpdateSortOrder(_recycleBinNavigationStructure.Count);
        return _recycleBinNavigationStructure.TryAdd(nodeToRemove.Key, nodeToRemove) &&
               _navigationStructure.TryRemove(key, out _);
    }

    public bool Add(Guid key, Guid? parentKey = null, int? sortOrder = null)
    {
        NavigationNode? parentNode = null;
        if (parentKey.HasValue)
        {
            if (_navigationStructure.TryGetValue(parentKey.Value, out parentNode) is false)
            {
                return false; // Parent node doesn't exist
            }
        }
        else
        {
            _roots.Add(key);
        }

        // Note: sortOrder can't be automatically determined for items at root level, so it needs to be passed in
        var newNode = new NavigationNode(key, sortOrder ?? 0);
        if (_navigationStructure.TryAdd(key, newNode) is false)
        {
            return false; // Node with this key already exists
        }

        parentNode?.AddChild(_navigationStructure, key);

        return true;
    }

    public bool Move(Guid key, Guid? targetParentKey = null)
    {
        if (_navigationStructure.TryGetValue(key, out NavigationNode? nodeToMove) is false)
        {
            return false; // Node doesn't exist
        }

        if (key == targetParentKey)
        {
            return false; // Cannot move a node to itself
        }

        _roots.Remove(key); // Just in case

        NavigationNode? targetParentNode = null;
        if (targetParentKey.HasValue)
        {
            if (_navigationStructure.TryGetValue(targetParentKey.Value, out targetParentNode) is false)
            {
                return false; // Target parent doesn't exist
            }
        }
        else
        {
            _roots.Add(key);
        }

        // Remove the node from its current parent's children list
        if (nodeToMove.Parent is not null && _navigationStructure.TryGetValue(nodeToMove.Parent.Value, out NavigationNode? currentParentNode))
        {
            currentParentNode.RemoveChild(_navigationStructure, key);
        }

        // Set the new parent for the node (if parent node is null - the node is moved to root)
        targetParentNode?.AddChild(_navigationStructure, key);

        return true;
    }

    public bool UpdateSortOrder(Guid key, int newSortOrder)
    {
        if (_navigationStructure.TryGetValue(key, out NavigationNode? node) is false)
        {
            return false; // Node doesn't exist
        }

        node.UpdateSortOrder(newSortOrder);

        return true;
    }

    public bool RemoveFromBin(Guid key)
    {
        if (TryRemoveNodeFromParentInStructure(_recycleBinNavigationStructure, key, out NavigationNode? nodeToRemove) is false || nodeToRemove is null)
        {
            return false; // Node doesn't exist
        }

        _recycleBinRoots.Remove(key);

        RemoveDescendantsRecursively(nodeToRemove);

        return _recycleBinNavigationStructure.TryRemove(key, out _);
    }

    public bool RestoreFromBin(Guid key, Guid? targetParentKey = null)
    {
        if (_recycleBinNavigationStructure.TryGetValue(key, out NavigationNode? nodeToRestore) is false)
        {
            return false; // Node doesn't exist
        }

        // If a target parent is specified, try to find it in the main structure
        NavigationNode? targetParentNode = null;
        if (targetParentKey.HasValue && _navigationStructure.TryGetValue(targetParentKey.Value, out targetParentNode) is false)
        {
            return false; // Target parent doesn't exist
        }

        // Set the new parent for the node (if parent node is null - the node is moved to root)
        targetParentNode?.AddChild(_recycleBinNavigationStructure, key);

        // Restore the node and its descendants from the recycle bin to the main structure
        RestoreNodeAndDescendantsRecursively(nodeToRestore);

        return _navigationStructure.TryAdd(nodeToRestore.Key, nodeToRestore) &&
               _recycleBinNavigationStructure.TryRemove(key, out _);
    }

    /// <summary>
    ///     Rebuilds the navigation structure based on the specified object type key and whether the items are trashed.
    ///     Only relevant for items in the content and media trees (which have readLock values of -333 or -334).
    /// </summary>
    /// <param name="readLock">The read lock value, should be -333 or -334 for content and media trees.</param>
    /// <param name="objectTypeKey">The key of the object type to rebuild.</param>
    /// <param name="trashed">Indicates whether the items are in the recycle bin.</param>
    protected async Task HandleRebuildAsync(int readLock, Guid objectTypeKey, bool trashed)
    {
        // This is only relevant for items in the content and media trees
        if (readLock != Constants.Locks.ContentTree && readLock != Constants.Locks.MediaTree)
        {
            return;
        }

        using ICoreScope scope = _coreScopeProvider.CreateCoreScope(autoComplete: true);
        scope.ReadLock(readLock);

        // Build the corresponding navigation structure
        if (trashed)
        {
            IEnumerable<INavigationModel> navigationModels = _navigationRepository.GetTrashedContentNodesByObjectType(objectTypeKey);
            BuildNavigationDictionary(_recycleBinNavigationStructure, _recycleBinRoots,  navigationModels);
        }
        else
        {
            IEnumerable<INavigationModel> navigationModels = _navigationRepository.GetContentNodesByObjectType(objectTypeKey);
            BuildNavigationDictionary(_navigationStructure, _roots, navigationModels);
        }
    }

    private bool TryGetParentKeyFromStructure(ConcurrentDictionary<Guid, NavigationNode> structure, Guid childKey, out Guid? parentKey)
    {
        if (structure.TryGetValue(childKey, out NavigationNode? childNode))
        {
            parentKey = childNode.Parent;
            return true;
        }

        // Child doesn't exist
        parentKey = null;
        return false;
    }

    private bool TryGetRootKeysFromStructure(IList<Guid> input, out IEnumerable<Guid> rootKeys)
    {
        // TODO can we make this more efficient?
        // Sort by SortOrder
        rootKeys = input
            .OrderBy(key => _navigationStructure[key].SortOrder)
            .ToList();

        return true;
    }

    private bool TryGetChildrenKeysFromStructure(ConcurrentDictionary<Guid, NavigationNode> structure, Guid parentKey, out IEnumerable<Guid> childrenKeys)
    {
        if (structure.TryGetValue(parentKey, out NavigationNode? parentNode) is false)
        {
            // Parent doesn't exist
            childrenKeys = [];
            return false;
        }

        // Keep children keys ordered based on their SortOrder
        childrenKeys = GetOrderedChildren(parentNode, structure).ToList();

        return true;
    }

    private bool TryGetDescendantsKeysFromStructure(ConcurrentDictionary<Guid, NavigationNode> structure, Guid parentKey, out IEnumerable<Guid> descendantsKeys)
    {
        var descendants = new List<Guid>();

        if (structure.TryGetValue(parentKey, out NavigationNode? parentNode) is false)
        {
            // Parent doesn't exist
            descendantsKeys = [];
            return false;
        }

        GetDescendantsRecursively(structure, parentNode, descendants);

        descendantsKeys = descendants;
        return true;
    }

    private bool TryGetAncestorsKeysFromStructure(ConcurrentDictionary<Guid, NavigationNode> structure, Guid childKey, out IEnumerable<Guid> ancestorsKeys)
    {
        var ancestors = new List<Guid>();

        if (structure.TryGetValue(childKey, out NavigationNode? node) is false)
        {
            // Child doesn't exist
            ancestorsKeys = [];
            return false;
        }

        while (node.Parent is not null && structure.TryGetValue(node.Parent.Value, out node))
        {
            ancestors.Add(node.Key);
        }

        ancestorsKeys = ancestors;
        return true;
    }

    private bool TryGetSiblingsKeysFromStructure(ConcurrentDictionary<Guid, NavigationNode> structure, Guid key, out IEnumerable<Guid> siblingsKeys)
    {
        siblingsKeys = [];

        if (structure.TryGetValue(key, out NavigationNode? node) is false)
        {
            return false; // Node doesn't exist
        }

        if (node.Parent is null)
        {
            // To find siblings of a node at root level, we need to iterate over all items and add those with null Parent
            siblingsKeys = structure
                .Where(kv => kv.Value.Parent is null && kv.Key != key)
                .OrderBy(kv => kv.Value.SortOrder)
                .Select(kv => kv.Key)
                .ToList();
            return true;
        }

        if (TryGetChildrenKeys(node.Parent.Value, out IEnumerable<Guid> childrenKeys) is false)
        {
            return false; // Couldn't retrieve children keys
        }

        // Filter out the node itself to get its siblings
        siblingsKeys = childrenKeys.Where(childKey => childKey != key).ToList();
        return true;
    }

    private void GetDescendantsRecursively(ConcurrentDictionary<Guid, NavigationNode> structure, NavigationNode node, List<Guid> descendants)
    {
        var childrenKeys = GetOrderedChildren(node, structure).ToList();
        foreach (Guid childKey in childrenKeys)
        {
            descendants.Add(childKey);

            // Retrieve the child node and its descendants
            if (structure.TryGetValue(childKey, out NavigationNode? childNode))
            {
                GetDescendantsRecursively(structure, childNode, descendants);
            }
        }
    }

    private bool TryRemoveNodeFromParentInStructure(ConcurrentDictionary<Guid, NavigationNode> structure, Guid key, out NavigationNode? nodeToRemove)
    {
        if (structure.TryGetValue(key, out nodeToRemove) is false)
        {
            return false; // Node doesn't exist
        }

        // Remove the node from its parent's children list
        if (nodeToRemove.Parent is not null && structure.TryGetValue(nodeToRemove.Parent.Value, out NavigationNode? parentNode))
        {
            parentNode.RemoveChild(structure, key);
        }

        return true;
    }

    private void AddDescendantsToRecycleBinRecursively(NavigationNode node)
    {
        _recycleBinRoots.Add(node.Key);
        _roots.Remove(node.Key);
        var childrenKeys = GetOrderedChildren(node, _navigationStructure).ToList();

        foreach (Guid childKey in childrenKeys)
        {
            if (_navigationStructure.TryGetValue(childKey, out NavigationNode? childNode) is false)
            {
                continue;
            }

            // Reset the SortOrder based on its new position in the bin
            childNode.UpdateSortOrder(_recycleBinNavigationStructure.Count);
            AddDescendantsToRecycleBinRecursively(childNode);

            // Only remove the child from the main structure if it was successfully added to the recycle bin
            if (_recycleBinNavigationStructure.TryAdd(childKey, childNode))
            {
                _navigationStructure.TryRemove(childKey, out _);
            }
        }
    }

    private void RemoveDescendantsRecursively(NavigationNode node)
    {
        var childrenKeys = GetOrderedChildren(node, _recycleBinNavigationStructure).ToList();
        foreach (Guid childKey in childrenKeys)
        {
            if (_recycleBinNavigationStructure.TryGetValue(childKey, out NavigationNode? childNode) is false)
            {
                continue;
            }

            RemoveDescendantsRecursively(childNode);
            _recycleBinNavigationStructure.TryRemove(childKey, out _);
        }
    }

    private void RestoreNodeAndDescendantsRecursively(NavigationNode node)
    {
        if (node.Parent is null)
        {
            _roots.Add(node.Key);
        }

        _recycleBinRoots.Remove(node.Key);
        var childrenKeys = GetOrderedChildren(node, _recycleBinNavigationStructure).ToList();

        foreach (Guid childKey in childrenKeys)
        {
            if (_recycleBinNavigationStructure.TryGetValue(childKey, out NavigationNode? childNode) is false)
            {
                continue;
            }

            RestoreNodeAndDescendantsRecursively(childNode);

            // Only remove the child from the recycle bin structure if it was successfully added to the main one
            if (_navigationStructure.TryAdd(childKey, childNode))
            {
                _recycleBinNavigationStructure.TryRemove(childKey, out _);
            }
        }
    }

    private IEnumerable<Guid> GetOrderedChildren(NavigationNode node, ConcurrentDictionary<Guid, NavigationNode> structure)
        => node.Children
            .Where(structure.ContainsKey)
            .OrderBy(childKey => structure[childKey].SortOrder)
            .ToList();

    private static void BuildNavigationDictionary(ConcurrentDictionary<Guid, NavigationNode> nodesStructure, IList<Guid> roots, IEnumerable<INavigationModel> entities)
    {
        var entityList = entities.ToList();
        var idToKeyMap = entityList.ToDictionary(x => x.Id, x => x.Key);

        foreach (INavigationModel entity in entityList)
        {
            var node = new NavigationNode(entity.Key, entity.SortOrder);
            nodesStructure[entity.Key] = node;

            // We don't set the parent for items under root, it will stay null
            if (entity.ParentId == -1)
            {
                roots.Add(entity.Key);
                continue;
            }

            if (idToKeyMap.TryGetValue(entity.ParentId, out Guid parentKey) is false)
            {
                continue;
            }

            // If the parent node exists in the nodesStructure, add the node to the parent's children (parent is set as well)
            if (nodesStructure.TryGetValue(parentKey, out NavigationNode? parentNode))
            {
                parentNode.AddChild(nodesStructure, entity.Key);
            }
        }
    }
}
