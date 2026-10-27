using PdfObjectKit.Models;
using PdfObjectKit.Options;
using PdfObjectKit.Validators;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Graphs;

/// <summary>Copies only reachable page dependencies, with a separate identity map per input.</summary>
internal sealed class PdfPageCopier
{
    private readonly PdfPageAssemblyOptions options;
    private readonly CancellationToken cancellationToken;
    private string? outputIntentFingerprint;
    private long encodedBytes;
    private int nodes;
    private readonly HashSet<string> formNames = [];
    private int sourceIndex;
    private int nextStructureParentKey;
    private bool hasUntaggedPages;
    private readonly PdfValue kids = Array();
    private readonly PdfValue pages;
    private readonly PdfObjectDocument result;

    public PdfPageCopier(PdfPageAssemblyOptions? options = null, CancellationToken cancellationToken = default)
    {
        this.options = options ?? new();
        this.options.Validate();
        this.cancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        pages = Dict(("Type", Name("Pages")), ("Kids", kids), ("Count", Num(0)));
        result = new()
        {
            PdfVersion = "1.0",
            Trailer = Dict(("Root", Ref(new(1)))),
            Objects =
            [
                new(new(1), Dict(("Type", Name("Catalog")), ("Pages", Ref(new(2))))),
                new(new(2), pages)
            ]
        };
    }

    public void Append(PdfObjectDocument source, IReadOnlyList<int> pageNumbers)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Objects.Count > options.MaxObjects)
        {
            throw new InvalidDataException("Source exceeds the assembly object limit.");
        }
        // Clone mutable containers directly; immutable base64 strings can be shared safely.
        var cloneNodes = 0;
        long sourceBytes = 0;
        PdfValue Clone(PdfValue value, int depth = 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value is null || depth > 100)
            {
                throw new InvalidDataException("Null node or excessive direct nesting in source.");
            }
            if (value.Type == "stream" && value.Base64 is { } stream)
            {
                sourceBytes += (long)stream.Length / 4 * 3 - (stream.EndsWith("==") ? 2 : stream.EndsWith('=') ? 1 : 0);
                if (sourceBytes > options.MaxEncodedStreamBytes)
                {
                    throw new InvalidDataException("Source exceeds the assembly encoded stream byte limit.");
                }
            }
            if (++cloneNodes > options.MaxNodes)
            {
                throw new InvalidDataException("Source exceeds the assembly node limit.");
            }
            return new()
            {
                Type = value.Type, Number = value.Number, Boolean = value.Boolean, Name = value.Name,
                Base64 = value.Base64, Reference = value.Reference,
                Items = value.Items?.Select(item => Clone(item, depth + 1)).ToList(),
                Entries = value.Entries?.ToDictionary(item => item.Key, item => Clone(item.Value, depth + 1))
            };
        }
        source = new()
        {
            Format = source.Format,
            SchemaVersion = source.SchemaVersion,
            PdfVersion = source.PdfVersion,
            Trailer = Clone(source.Trailer),
            Objects = source.Objects.Select(item => new PdfIndirectObject(item.Id, Clone(item.Value))).ToList()
        };
        PdfDocumentValidator.Validate(source);
        sourceIndex++;
        if (source.Trailer.Entries!.ContainsKey("Encrypt"))
        {
            throw new NotSupportedException("Encrypted documents must be decrypted before copying pages.");
        }
        var graph = new PdfGraph(source);
        var catalog = graph.Resolve(source.Trailer.Entries!["Root"]);
        if (options.NamespaceFormFields)
        {
            void CheckScripts(PdfValue value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (value.Entries?.ContainsKey("JS") == true || graph.Get(value, "S")?.Name == "JavaScript")
                {
                    throw new NotSupportedException("Cannot namespace fields in documents containing JavaScript.");
                }
                foreach (var child in value.Entries?.Values ?? Enumerable.Empty<PdfValue>())
                {
                    if (child.Reference is null)
                    {
                        CheckScripts(child);
                    }
                }
                foreach (var child in value.Items ?? [])
                {
                    if (child.Reference is null)
                    {
                        CheckScripts(child);
                    }
                }
            }
            foreach (var item in source.Objects)
            {
                CheckScripts(item.Value);
            }
        }
        foreach (var key in new[] { "Perms", "AA", "Collection", "Threads", "Requirements" })
        {
            if (catalog.Entries!.ContainsKey(key))
            {
                throw new NotSupportedException($"Page extraction/merging does not yet support catalog /{key}.");
            }
        }
        var supportedCatalogKeys = new HashSet<string>
        {
            "Type", "Pages", "Version", "AcroForm", "Outlines", "Names", "Dests", "PageLabels",
            "OCProperties", "AF", "Metadata", "Lang", "ViewerPreferences", "PageLayout", "PageMode", "OpenAction", "OutputIntents", "StructTreeRoot", "MarkInfo"
        };
        var unknownCatalogKey = catalog.Entries!.Keys.FirstOrDefault(key => !supportedCatalogKeys.Contains(key));
        if (unknownCatalogKey is not null)
        {
            throw new NotSupportedException($"Catalog /{unknownCatalogKey} has no assembly preservation policy.");
        }
        string Fingerprint(PdfValue? root)
        {
            if (root is null)
            {
                return "";
            }
            var seen = new HashSet<PdfValue>();
            var fingerprintNodes = 0;
            PdfValue Expand(PdfValue raw, int depth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth > 100 || ++fingerprintNodes > options.MaxNodes)
                {
                    throw new InvalidDataException("Output intent dependencies exceed assembly limits.");
                }
                var value = graph.Resolve(raw);
                if (!seen.Add(value))
                {
                    throw new InvalidDataException("Cyclic output intent dependency.");
                }
                var expanded = new PdfValue
                {
                    Type = value.Type, Name = value.Name, Number = value.Number, Boolean = value.Boolean,
                    Base64 = value.Base64, Items = value.Items?.Select(item => Expand(item, depth + 1)).ToList(),
                    Entries = value.Entries?.Where(item => value.Type != "stream" || item.Key != "Length").OrderBy(item => item.Key, StringComparer.Ordinal)
                        .ToDictionary(item => item.Key, item => Expand(item.Value, depth + 1))
                };
                seen.Remove(value);
                return expanded;
            }
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Expand(root, 0));
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        }
        var intentFingerprint = Fingerprint(graph.Get(catalog, "OutputIntents"));
        if (sourceIndex == 1)
        {
            outputIntentFingerprint = intentFingerprint;
        }
        else if (outputIntentFingerprint != intentFingerprint)
        {
            throw new NotSupportedException("Output intents differ between inputs (including missing intents). Convert sources to a consistent color policy before merging.");
        }
        var pageOffset = kids.Items!.Count;
        if (pageNumbers.Count > options.MaxPages - pageOffset)
        {
            throw new InvalidDataException("Result exceeds the assembly page limit.");
        }
        var sourcePages = graph.Pages();
        if (pageNumbers.Count == 0 || pageNumbers.Any(number => number < 1 || number > sourcePages.Count)
            || pageNumbers.Distinct().Count() != pageNumbers.Count)
        {
            throw new ArgumentException("Choose distinct, one-based page numbers within the document.");
        }
        var version = graph.Get(catalog, "Version")?.Name ?? source.PdfVersion;
        if (!Version.TryParse(version, out var parsedVersion) || parsedVersion > new Version(2, 0))
        {
            throw new NotSupportedException("Unsupported catalog PDF version.");
        }
        if (parsedVersion > Version.Parse(result.PdfVersion))
        {
            result.PdfVersion = version;
        }
        if (Version.Parse(source.PdfVersion) > Version.Parse(result.PdfVersion))
        {
            result.PdfVersion = source.PdfVersion;
        }

        var selectedSet = pageNumbers.Select(number => sourcePages[number - 1].Dictionary).ToHashSet();
        var form = PdfPageAssemblyForms.Prepare(source, selectedSet, sourceIndex, formNames, options.NamespaceFormFields);
        if (graph.Get(catalog, "StructTreeRoot") is null && graph.Get(catalog, "MarkInfo") is { } sourceMarkInfo && graph.Get(sourceMarkInfo, "Marked")?.Boolean == true)
        {
            throw new InvalidDataException("Document claims tagging but has no structure tree.");
        }
        var structure = graph.Get(catalog, "StructTreeRoot") is null ? null
            : new PdfPageAssemblyStructure(source, selectedSet, sourceIndex, options.MaxNodes, cancellationToken);
        if (structure is null)
        {
            hasUntaggedPages = true;
        }
        var navigation = new PdfPageAssemblyNavigation(source, selectedSet);
        navigation.PrepareOutlines();
        var objects = source.Objects.ToDictionary(item => item.Id, item => item.Value);
        var references = new Dictionary<PdfReference, PdfReference>();
        var selectedPages = new Dictionary<PdfValue, PdfReference>();
        var targets = new List<(PdfValue Source, PdfValue Target)>();
        PdfReference Add(PdfValue value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Objects.Count >= options.MaxObjects)
            {
                throw new InvalidDataException("Result exceeds the assembly object limit.");
            }
            var id = new PdfReference(result.Objects.Count + 1);
            result.Objects.Add(new(id, value));
            return id;
        }
        foreach (var number in pageNumbers)
        {
            var original = sourcePages[number - 1].Dictionary;
            var target = Dict();
            var id = Add(target);
            selectedPages.Add(original, id);
            targets.Add((original, target));
            kids.Items!.Add(Ref(id));
        }

        PdfValue? structureRootReference = null;
        PdfValue? outputStructureRoot = null;
        if (structure is not null)
        {
            var structureCatalog = result.Objects[0].Value;
            if (!structureCatalog.Entries!.TryGetValue("StructTreeRoot", out structureRootReference))
            {
                outputStructureRoot = Dict(("Type", Name("StructTreeRoot")), ("K", Array()),
                    ("ParentTree", Dict(("Nums", Array()))));
                structureRootReference = Ref(Add(outputStructureRoot));
                structureCatalog.Entries["StructTreeRoot"] = structureRootReference;
            }
            else
            {
                outputStructureRoot = new PdfGraph(result).Resolve(structureRootReference);
            }
            if (!structureCatalog.Entries.ContainsKey("MarkInfo"))
            {
                structureCatalog.Entries["MarkInfo"] = Dict();
            }
            var markInfo = structureCatalog.Entries["MarkInfo"];
            markInfo.Entries!["Marked"] = new() { Type = "boolean", Boolean = true };
            foreach (var key in new[] { "Suspects", "UserProperties" })
            {
                if (graph.Get(catalog, "MarkInfo") is { } sourceMark && graph.Get(sourceMark, key)?.Boolean == true)
                {
                    markInfo.Entries[key] = new() { Type = "boolean", Boolean = true };
                }
            }
        }
        if (hasUntaggedPages && result.Objects[0].Value.Entries!.TryGetValue("MarkInfo", out var mixedMark))
        {
            mixedMark.Entries!["Suspects"] = new() { Type = "boolean", Boolean = true };
        }
        PdfValue MapStructureParent(PdfValue owner, PdfValue value, bool array)
        {
            if (structure is null)
            {
                throw new InvalidDataException("Structural parent key exists without a structure tree.");
            }
            return structure.MapParent(owner, value, array, () => checked(nextStructureParentKey++));
        }

        PdfValue Copy(PdfValue value, int depth = 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++nodes > options.MaxNodes)
            {
                throw new InvalidDataException("Result exceeds the assembly node limit.");
            }
            if (value.Type == "stream")
            {
                var base64 = value.Base64!;
                encodedBytes += (long)base64.Length / 4 * 3 - (base64.EndsWith("==") ? 2 : base64.EndsWith('=') ? 1 : 0);
                if (encodedBytes > options.MaxEncodedStreamBytes)
                {
                    throw new InvalidDataException("Result exceeds the assembly encoded stream byte limit.");
                }
            }
            if (depth > 100)
            {
                throw new InvalidDataException("Page dependency nesting exceeds the supported limit.");
            }
            if (value.Reference is { } reference)
            {
                if (references.TryGetValue(reference, out var existing))
                {
                    return Ref(existing);
                }
                var original = objects[reference];
                if (structure is not null && ReferenceEquals(original, structure.Root))
                {
                    return structureRootReference!;
                }
                if (selectedPages.TryGetValue(original, out var pageId))
                {
                    references.Add(reference, pageId);
                    return Ref(pageId);
                }
                var type = graph.Get(original, "Type")?.Name;
                if (type is "Page" or "Pages" or "Catalog")
                {
                    throw new NotSupportedException("A page dependency or destination refers outside the selected pages.");
                }
                var placeholder = new PdfValue { Type = "null" };
                var id = Add(placeholder);
                references.Add(reference, id);
                var copied = Copy(original, depth + 1);
                var index = checked((int)id.ObjectNumber - 1);
                result.Objects[index] = new(id, copied);
                return Ref(id);
            }
            if (selectedPages.TryGetValue(value, out var selectedPage))
            {
                return Ref(selectedPage);
            }
            if (structure is not null && ReferenceEquals(value, structure.Root))
            {
                return structureRootReference!;
            }
            var entryType = graph.Get(value, "Type")?.Name;
            if (entryType is "Page" or "Pages" or "Catalog" or "Sig")
            {
                throw new NotSupportedException("Unsupported page-tree, signature, widget or structure dependency.");
            }
            return new PdfValue
            {
                Type = value.Type,
                Number = value.Number,
                Boolean = value.Boolean,
                Name = value.Name,
                Base64 = value.Base64,
                Items = value.Items?.Select(item => Copy(item, depth + 1)).ToList(),
                Entries = value.Entries?.ToDictionary(item => item.Key, item =>
                    item.Key is "StructParent" or "StructParents" ? MapStructureParent(value, item.Value, item.Key == "StructParents") :
                    Copy(item.Key == "Dest" || item.Key == "D" && graph.Get(value, "S")?.Name == "GoTo"
                        ? navigation.ResolveDestination(item.Value) : item.Value, depth + 1))
            };
        }

        foreach (var (original, target) in targets)
        {
            foreach (var entry in original.Entries!)
            {
                if (entry.Key != "Parent")
                {
                    if (entry.Key == "B")
                    {
                        throw new NotSupportedException("Article beads are not supported.");
                    }
                    target.Entries![entry.Key] = entry.Key is "StructParents" or "StructParent"
                        ? MapStructureParent(original, entry.Value, entry.Key == "StructParents") : Copy(entry.Value);
                }
            }
            // These four page attributes may be inherited from any ancestor in the source tree.
            foreach (var key in new[] { "Resources", "MediaBox", "CropBox", "Rotate" })
            {
                var ancestor = original;
                var seen = new HashSet<PdfValue>();
                while (!target.Entries!.ContainsKey(key) && seen.Add(ancestor))
                {
                    if (ancestor.Entries!.TryGetValue(key, out var inherited))
                    {
                        target.Entries[key] = Copy(inherited);
                        break;
                    }
                    if (graph.Get(ancestor, "Parent") is not { } parent)
                    {
                        break;
                    }
                    ancestor = parent;
                }
            }
            target.Entries!["Parent"] = Ref(new(2));
        }
        var outputCatalog = result.Objects[0].Value;
        if (structure is not null)
        {
            outputStructureRoot!.Entries!["K"].Items!.AddRange(structure.Root.Entries!["K"].Items!.Select(item => Copy(item)));
            foreach (var key in new[] { "RoleMap", "ClassMap" })
            {
                if (graph.Get(structure.Root, key) is { } map)
                {
                    if (!outputStructureRoot.Entries.ContainsKey(key))
                    {
                        outputStructureRoot.Entries[key] = Dict();
                    }
                    foreach (var entry in map.Entries!)
                    {
                        outputStructureRoot.Entries[key].Entries!.Add(entry.Key, Copy(entry.Value));
                    }
                }
            }
            foreach (var key in new[] { "Namespaces", "AF" })
            {
                if (graph.Get(structure.Root, key)?.Items is { } items)
                {
                    if (!outputStructureRoot.Entries.ContainsKey(key))
                    {
                        outputStructureRoot.Entries[key] = Array();
                    }
                    outputStructureRoot.Entries[key].Items!.AddRange(items.Select(item => Copy(item)));
                }
            }
            if (graph.Get(structure.Root, "IDTree") is { } idTree)
            {
                if (!outputStructureRoot.Entries.ContainsKey("IDTree"))
                {
                    outputStructureRoot.Entries["IDTree"] = Dict(("Names", Array()));
                }
                var ids = outputStructureRoot.Entries["IDTree"].Entries!["Names"].Items!;
                ids.AddRange(graph.Get(idTree, "Names")!.Items!.Select(item => Copy(item)));
                var pairs = Enumerable.Range(0, ids.Count / 2).Select(i => (Key: ids[i * 2], Value: ids[i * 2 + 1]))
                    .OrderBy(item => Convert.ToHexString(Convert.FromBase64String(item.Key.Base64!)), StringComparer.Ordinal).ToList();
                ids.Clear();
                ids.AddRange(pairs.SelectMany(pair => new[] { pair.Key, pair.Value }));
            }
            structure.AppendParents(outputStructureRoot, item => Copy(item));
            outputStructureRoot.Entries["ParentTreeNextKey"] = Num(nextStructureParentKey);
        }
        if (form is not null)
        {
            PdfPageAssemblyForms.Merge(outputCatalog, Copy(form));
        }
        if (catalog.Entries!.TryGetValue("Outlines", out var outlines))
        {
            navigation.MergeOutlines(result, Copy(outlines));
        }
        if (graph.Get(catalog, "PageLabels") is { } labels)
        {
            var definitions = new SortedDictionary<int, PdfValue>();
            var visitedLabels = new HashSet<PdfValue>();
            void ReadLabels(PdfValue raw, int depth)
            {
                var node = graph.Resolve(raw);
                if (depth > 100 || !visitedLabels.Add(node))
                {
                    throw new InvalidDataException("Invalid page label number tree.");
                }
                var numbers = graph.Get(node, "Nums")?.Items ?? [];
                if (numbers.Count % 2 != 0)
                {
                    throw new InvalidDataException("Page label tree must contain key/value pairs.");
                }
                for (var i = 0; i + 1 < numbers.Count; i += 2)
                {
                    var labelIndex = Number(graph.Resolve(numbers[i]));
                    var label = graph.Resolve(numbers[i + 1]);
                    if (labelIndex != Math.Truncate(labelIndex) || labelIndex < 0 || labelIndex >= sourcePages.Count || label.Type != "dictionary")
                    {
                        throw new InvalidDataException("Invalid page label index or dictionary.");
                    }
                    var start = graph.Number(label, "St", 1);
                    if (start < 1 || start != Math.Truncate(start)
                        || graph.Get(label, "S")?.Name is { } style && style is not ("D" or "R" or "r" or "A" or "a"))
                    {
                        throw new InvalidDataException("Invalid page label start or style.");
                    }
                    if (!definitions.TryAdd((int)labelIndex, label))
                    {
                        throw new InvalidDataException("Duplicate page label index.");
                    }
                }
                foreach (var child in graph.Get(node, "Kids")?.Items ?? [])
                {
                    ReadLabels(child, depth + 1);
                }
            }
            ReadLabels(labels, 0);
            if (!definitions.ContainsKey(0))
            {
                throw new InvalidDataException("Page labels must define index zero.");
            }
            var labelKeys = definitions.Keys.ToArray();
            if (!outputCatalog.Entries!.TryGetValue("PageLabels", out var outputLabels))
            {
                outputLabels = Dict(("Nums", Array()));
                outputCatalog.Entries["PageLabels"] = outputLabels;
                if (pageOffset > 0)
                {
                    outputLabels.Entries!["Nums"].Items!.AddRange([Num(0), Dict(("S", Name("D")))]);
                }
            }
            for (var i = 0; i < pageNumbers.Count; i++)
            {
                var originalIndex = pageNumbers[i] - 1;
                cancellationToken.ThrowIfCancellationRequested();
                var position = System.Array.BinarySearch(labelKeys, originalIndex);
                if (position < 0)
                {
                    position = ~position - 1;
                }
                var definition = new KeyValuePair<int, PdfValue>(labelKeys[position], definitions[labelKeys[position]]);
                var label = definition.Value is null ? Dict(("S", Name("D"))) : Copy(definition.Value);
                if (label.Entries!.ContainsKey("S"))
                {
                    label.Entries["St"] = Num((definition.Value is null ? 1 : graph.Number(definition.Value, "St", 1)) + originalIndex - definition.Key);
                }
                outputLabels.Entries!["Nums"].Items!.AddRange([Num(pageOffset + i), label]);
            }
        }
        else if (outputCatalog.Entries!.TryGetValue("PageLabels", out var previousLabels))
        {
            previousLabels.Entries!["Nums"].Items!.AddRange([Num(pageOffset), Dict(("S", Name("D")), ("St", Num(1)))]);
        }
        if (graph.Get(catalog, "OCProperties") is { } layers)
        {
            var configuration = graph.Get(layers, "D") ?? Dict();
            if (layers.Entries!.Keys.Any(key => key is not ("OCGs" or "D" or "Configs"))
                || configuration.Entries!.Keys.Any(key => key is not ("Name" or "Creator" or "BaseState" or "ON" or "OFF" or "Intent" or "Order" or "ListMode" or "RBGroups" or "Locked" or "AS")))
            {
                throw new NotSupportedException("Unknown optional-content configuration cannot be preserved safely.");
            }
            var intent = graph.Get(configuration, "Intent");
            var intents = intent?.Type == "array" ? intent.Items!.Select(item => graph.Resolve(item).Name).ToList() : new List<string?> { intent?.Name ?? "View" };
            if (intents.Any(name => name != "View"))
            {
                throw new NotSupportedException("Only View-intent layer configurations are currently supported.");
            }
            if (graph.Get(layers, "Configs") is not null
                || graph.Get(configuration, "BaseState")?.Name == "Unchanged")
            {
                throw new NotSupportedException("Alternate or unchanged-state layer configurations require additional remapping.");
            }
            var groups = graph.Get(layers, "OCGs")?.Items ?? [];
            var on = (graph.Get(configuration, "ON")?.Items ?? []).Select(graph.Resolve).ToHashSet();
            var off = (graph.Get(configuration, "OFF")?.Items ?? []).Select(graph.Resolve).ToHashSet();
            var baseOff = graph.Get(configuration, "BaseState")?.Name == "OFF";
            if (!outputCatalog.Entries!.TryGetValue("OCProperties", out var outputLayers))
            {
                outputLayers = Dict(("OCGs", Array()), ("D", Dict(("BaseState", Name("ON")), ("OFF", Array()))));
                outputCatalog.Entries["OCProperties"] = outputLayers;
            }
            var outputConfiguration = outputLayers.Entries!["D"];
            foreach (var group in groups)
            {
                var copiedGroup = Copy(group);
                outputLayers.Entries["OCGs"].Items!.Add(copiedGroup);
                if (off.Contains(graph.Resolve(group)) || baseOff && !on.Contains(graph.Resolve(group)))
                {
                    outputConfiguration.Entries!["OFF"].Items!.Add(copiedGroup);
                }
            }
            if (graph.Get(configuration, "AS")?.Items is { } usageApplications)
            {
                if (!outputConfiguration.Entries!.ContainsKey("AS"))
                {
                    outputConfiguration.Entries["AS"] = Array();
                }
                foreach (var rawApplication in usageApplications)
                {
                    var application = graph.Resolve(rawApplication);
                    if (application.Type != "dictionary")
                    {
                        throw new InvalidDataException("Invalid optional-content usage application.");
                    }
                    var copiedApplication = Dict();
                    foreach (var entry in application.Entries!)
                    {
                        copiedApplication.Entries![entry.Key] = Copy(entry.Value);
                    }
                    if (!copiedApplication.Entries!.ContainsKey("OCGs"))
                    {
                        copiedApplication.Entries["OCGs"] = Array(groups.Select(item => Copy(item)).ToArray());
                    }
                    outputConfiguration.Entries["AS"].Items!.Add(copiedApplication);
                }
            }
            foreach (var key in new[] { "Order", "RBGroups", "Locked" })
            {
                if (graph.Get(configuration, key)?.Items is { } entries)
                {
                    if (!outputConfiguration.Entries!.ContainsKey(key))
                    {
                        outputConfiguration.Entries[key] = Array();
                    }
                    outputConfiguration.Entries[key].Items!.AddRange(entries.Select(item => Copy(item)));
                }
            }
        }
        if (graph.Get(catalog, "Names") is { } sourceNames && graph.Get(sourceNames, "EmbeddedFiles") is { } embeddedFiles)
        {
            if (!outputCatalog.Entries!.TryGetValue("Names", out var outputNames))
            {
                outputNames = Dict();
                outputCatalog.Entries["Names"] = outputNames;
            }
            if (!outputNames.Entries!.TryGetValue("EmbeddedFiles", out var outputFiles))
            {
                outputFiles = Dict(("Names", Array()));
                outputNames.Entries["EmbeddedFiles"] = outputFiles;
            }
            var entries = outputFiles.Entries!["Names"].Items!;
            var allFiles = new Dictionary<string, (PdfValue Key, PdfValue File)>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Count; i += 2)
            {
                allFiles.Add(Convert.ToHexString(Convert.FromBase64String(entries[i].Base64!)), (entries[i], entries[i + 1]));
            }
            var visitedFiles = new HashSet<PdfValue>();
            void ReadFiles(PdfValue raw, int depth)
            {
                var node = graph.Resolve(raw);
                if (depth > 100 || !visitedFiles.Add(node))
                {
                    throw new InvalidDataException("Invalid embedded file name tree.");
                }
                var names = graph.Get(node, "Names")?.Items ?? [];
                if (names.Count % 2 != 0)
                {
                    throw new InvalidDataException("Embedded file name tree must contain key/value pairs.");
                }
                for (var i = 0; i + 1 < names.Count; i += 2)
                {
                    var name = Copy(graph.Resolve(names[i]));
                    var key = Convert.ToHexString(Convert.FromBase64String(name.Base64!));
                    if (allFiles.ContainsKey(key))
                    {
                        if (!options.RenameConflictingAttachments)
                        {
                            throw new NotSupportedException("Duplicate attachment name. Enable RenameConflictingAttachments or rename it before merging.");
                        }
                        var originalName = PdfFormGraph.Text(name);
                        var suffix = 0;
                        do
                        {
                            name = Str(new byte[] { 254, 255 }.Concat(System.Text.Encoding.BigEndianUnicode.GetBytes($"Document{sourceIndex}_{++suffix}_{originalName}")).ToArray());
                            key = Convert.ToHexString(Convert.FromBase64String(name.Base64!));
                        }
                        while (allFiles.ContainsKey(key));
                    }
                    allFiles.Add(key, (name, Copy(names[i + 1])));
                }
                foreach (var child in graph.Get(node, "Kids")?.Items ?? [])
                {
                    ReadFiles(child, depth + 1);
                }
            }
            ReadFiles(embeddedFiles, 0);
            entries.Clear();
            foreach (var file in allFiles.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value))
            {
                entries.Add(file.Key);
                entries.Add(file.File);
            }
        }
        var retainedDestinations = navigation.RetainedDestinations().ToList();
        if (retainedDestinations.Count > 0)
        {
            if (!outputCatalog.Entries!.TryGetValue("Names", out var outputNames))
            {
                outputNames = Dict();
                outputCatalog.Entries["Names"] = outputNames;
            }
            if (!outputNames.Entries!.TryGetValue("Dests", out var outputDestinations))
            {
                outputDestinations = Dict(("Names", Array()));
                outputNames.Entries["Dests"] = outputDestinations;
            }
            var entries = outputDestinations.Entries!["Names"].Items!;
            var all = new Dictionary<string, PdfValue>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Count; i += 2)
            {
                all.Add(PdfFormGraph.Text(entries[i]), entries[i + 1]);
            }
            foreach (var item in retainedDestinations)
            {
                var name = item.Name;
                if (all.ContainsKey(name))
                {
                    if (!options.RenameConflictingDestinations)
                    {
                        throw new NotSupportedException("Duplicate named destination. Enable RenameConflictingDestinations or rename it before merging.");
                    }
                    var suffix = 0;
                    do
                    {
                        name = $"Document{sourceIndex}_{++suffix}_{item.Name}";
                    }
                    while (all.ContainsKey(name));
                }
                all.Add(name, Copy(item.Destination));
            }
            entries.Clear();
            var encoded = all.Select(item => (Key: Str(new byte[] { 254, 255 }.Concat(System.Text.Encoding.BigEndianUnicode.GetBytes(item.Key)).ToArray()), item.Value))
                .OrderBy(item => Convert.ToHexString(Convert.FromBase64String(item.Key.Base64!)), StringComparer.Ordinal);
            foreach (var item in encoded)
            {
                entries.Add(item.Key);
                entries.Add(item.Value);
            }
        }
        if (graph.Get(catalog, "AF")?.Items is { } associatedFiles)
        {
            if (!outputCatalog.Entries!.ContainsKey("AF"))
            {
                outputCatalog.Entries["AF"] = Array();
            }
            outputCatalog.Entries["AF"].Items!.AddRange(associatedFiles.Select(item => Copy(item)));
        }
        if (sourceIndex == 1)
        {
            if (graph.Get(catalog, "OpenAction") is { } openAction)
            {
                outputCatalog.Entries!["OpenAction"] = Copy(openAction.Type is "array" or "string" or "name"
                    ? navigation.ResolveDestination(openAction) : openAction);
            }
            foreach (var key in new[] { "OutputIntents", "Metadata", "Lang", "ViewerPreferences", "PageLayout", "PageMode" })
            {
                if (catalog.Entries.TryGetValue(key, out var value))
                {
                    outputCatalog.Entries![key] = Copy(value);
                }
            }
            if (source.Trailer.Entries!.TryGetValue("Info", out var info))
            {
                result.Trailer.Entries!["Info"] = Copy(info);
            }
        }
    }

    public PdfObjectDocument Finish()
    {
        cancellationToken.ThrowIfCancellationRequested();
        pages.Entries!["Count"] = Num(kids.Items!.Count);
        PdfDocumentValidator.Validate(result);
        return result;
    }
}
