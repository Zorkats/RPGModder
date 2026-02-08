using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Services;

// Intelligently merges JSON data from multiple mods (like Wrye Bash for Bethesda games).
// Instead of "last mod wins entirely", this merges at the record/field level.
public class JsonMergeService
{
    public MergeReport LastReport { get; private set; } = new();

    // Merges multiple JSON sources into one, with intelligent conflict resolution.
    // Sources should be ordered by load priority (first = lowest, last = highest).
    public string MergeJsonFiles(string baseJson, IEnumerable<string> modJsons, string fileName)
    {
        LastReport = new MergeReport { FileName = fileName };

        try
        {
            var baseToken = JToken.Parse(baseJson);

            // Determine merge strategy based on structure
            if (baseToken is JArray baseArray)
            {
                // RPG Maker data files (Actors.json, Items.json, etc.)
                return MergeArrayBased(baseArray, modJsons);
            }
            else if (baseToken is JObject baseObject)
            {
                // Config files (System.json, etc.)
                return MergeObjectBased(baseObject, modJsons);
            }
            else
            {
                // Primitive - just use last value
                LastReport.Strategy = "Overwrite (primitive)";
                string result = baseJson;
                foreach (var modJson in modJsons)
                    result = modJson;
                return result;
            }
        }
        catch (Exception ex)
        {
            LastReport.Errors.Add($"Merge failed: {ex.Message}");
            // Fall back to last mod wins
            return modJsons.LastOrDefault() ?? baseJson;
        }
    }

    // Merges array-based data (like Actors.json, Items.json, etc.)
    // Each array element is treated as a record with an implicit ID (index).
    private string MergeArrayBased(JArray baseArray, IEnumerable<string> modJsons)
    {
        LastReport.Strategy = "Array merge (record-level)";

        // Track which indices have been modified and by whom
        var mergedArray = new JArray(baseArray); // Clone base
        var modifiedIndices = new Dictionary<int, List<string>>(); // index -> list of mod names that touched it

        int modIndex = 0;
        foreach (var modJson in modJsons)
        {
            modIndex++;
            string modName = $"Mod{modIndex}";

            try
            {
                var modArray = JArray.Parse(modJson);

                // Expand merged array if mod array is larger
                while (mergedArray.Count < modArray.Count)
                {
                    mergedArray.Add(JValue.CreateNull());
                }

                for (int i = 0; i < modArray.Count; i++)
                {
                    var baseElement = i < baseArray.Count ? baseArray[i] : null;
                    var modElement = modArray[i];

                    // Skip if mod element is same as base (no change)
                    if (JToken.DeepEquals(baseElement, modElement))
                        continue;

                    // Skip null entries
                    if (modElement == null || modElement.Type == JTokenType.Null)
                        continue;

                    // Track this modification
                    if (!modifiedIndices.ContainsKey(i))
                        modifiedIndices[i] = new List<string>();
                    modifiedIndices[i].Add(modName);

                    // Check if this index was already modified by another mod
                    if (modifiedIndices[i].Count > 1)
                    {
                        // Conflict! Try field-level merge if both are objects
                        var existingElement = mergedArray[i];
                        if (existingElement is JObject existingObj && modElement is JObject modObj)
                        {
                            // Attempt field-level merge
                            var mergeResult = MergeObjects(existingObj, modObj);
                            mergedArray[i] = mergeResult.Merged;
                            
                            if (mergeResult.FieldConflicts.Count > 0)
                            {
                                LastReport.Conflicts.Add(new MergeConflict
                                {
                                    Index = i,
                                    Field = string.Join(", ", mergeResult.FieldConflicts),
                                    Mods = new List<string>(modifiedIndices[i]),
                                    Resolution = "Last mod wins for conflicting fields"
                                });
                            }
                            else
                            {
                                LastReport.MergedRecords++;
                            }
                        }
                        else
                        {
                            // Can't merge - last wins
                            mergedArray[i] = modElement.DeepClone();
                            LastReport.Conflicts.Add(new MergeConflict
                            {
                                Index = i,
                                Mods = new List<string>(modifiedIndices[i]),
                                Resolution = "Last mod wins (non-object)"
                            });
                        }
                    }
                    else
                    {
                        // No conflict - just apply the change
                        mergedArray[i] = modElement.DeepClone();
                        LastReport.MergedRecords++;
                    }
                }
            }
            catch (Exception ex)
            {
                LastReport.Errors.Add($"{modName}: {ex.Message}");
            }
        }

        LastReport.TotalRecords = mergedArray.Count;
        return mergedArray.ToString(Formatting.Indented);
    }

    // Merges object-based data (like System.json)
    private string MergeObjectBased(JObject baseObject, IEnumerable<string> modJsons)
    {
        LastReport.Strategy = "Object merge (field-level)";

        var merged = (JObject)baseObject.DeepClone();

        foreach (var modJson in modJsons)
        {
            try
            {
                var modObject = JObject.Parse(modJson);
                var result = MergeObjects(merged, modObject);
                merged = result.Merged;
                
                foreach (var field in result.FieldConflicts)
                {
                    LastReport.Conflicts.Add(new MergeConflict
                    {
                        Field = field,
                        Resolution = "Last mod wins"
                    });
                }
                LastReport.MergedRecords += result.MergedFields;
            }
            catch (Exception ex)
            {
                LastReport.Errors.Add(ex.Message);
            }
        }

        return merged.ToString(Formatting.Indented);
    }

    // Deep merges two JObjects, tracking field-level conflicts
    private ObjectMergeResult MergeObjects(JObject target, JObject source)
    {
        var result = new ObjectMergeResult
        {
            Merged = (JObject)target.DeepClone()
        };

        foreach (var prop in source.Properties())
        {
            var existingProp = result.Merged.Property(prop.Name);

            if (existingProp == null)
            {
                // New property - just add it
                result.Merged.Add(prop.Name, prop.Value.DeepClone());
                result.MergedFields++;
            }
            else if (JToken.DeepEquals(existingProp.Value, prop.Value))
            {
                // Same value - no action needed
                continue;
            }
            else if (existingProp.Value is JObject existingObj && prop.Value is JObject sourceObj)
            {
                // Both are objects - recurse
                var nestedResult = MergeObjects(existingObj, sourceObj);
                existingProp.Value = nestedResult.Merged;
                result.FieldConflicts.AddRange(nestedResult.FieldConflicts.Select(f => $"{prop.Name}.{f}"));
                result.MergedFields += nestedResult.MergedFields;
            }
            else if (existingProp.Value is JArray existingArr && prop.Value is JArray sourceArr)
            {
                // Both are arrays - for simple arrays, last wins
                // (Could implement smarter array merging here)
                existingProp.Value = sourceArr.DeepClone();
                result.FieldConflicts.Add(prop.Name);
            }
            else
            {
                // Different values - last wins, but track as conflict
                existingProp.Value = prop.Value.DeepClone();
                result.FieldConflicts.Add(prop.Name);
            }
        }

        return result;
    }

    private class ObjectMergeResult
    {
        public JObject Merged { get; set; } = new();
        public List<string> FieldConflicts { get; set; } = new();
        public int MergedFields { get; set; }
    }
}

public class MergeReport
{
    public string FileName { get; set; } = "";
    public string Strategy { get; set; } = "";
    public int TotalRecords { get; set; }
    public int MergedRecords { get; set; }
    public List<MergeConflict> Conflicts { get; set; } = new();
    public List<string> Errors { get; set; } = new();

    public bool HasConflicts => Conflicts.Count > 0;
    public bool HasErrors => Errors.Count > 0;

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Merge Report: {FileName} ===");
        sb.AppendLine($"Strategy: {Strategy}");
        sb.AppendLine($"Records: {MergedRecords} merged / {TotalRecords} total");
        
        if (Conflicts.Count > 0)
        {
            sb.AppendLine($"Conflicts ({Conflicts.Count}):");
            foreach (var c in Conflicts)
                sb.AppendLine($"  - Index {c.Index}, Field: {c.Field}, Resolution: {c.Resolution}");
        }
        
        if (Errors.Count > 0)
        {
            sb.AppendLine($"Errors ({Errors.Count}):");
            foreach (var e in Errors)
                sb.AppendLine($"  - {e}");
        }

        return sb.ToString();
    }
}

public class MergeConflict
{
    public int Index { get; set; } = -1;
    public string Field { get; set; } = "";
    public List<string> Mods { get; set; } = new();
    public string Resolution { get; set; } = "";
}
