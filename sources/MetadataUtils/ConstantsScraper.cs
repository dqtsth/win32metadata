﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace MetadataUtils
{
    public static class ConstantsScraper
    {
        public static ScraperResults ScrapeConstants(
            string[] enumJsonFiles,
            string defaultNamespace,
            string scraperOutputDir,
            string constantsHeaderText,
            HashSet<string> exclusionNames,
            Dictionary<string, string> traversedHeaderToNamespaceMap,
            Dictionary<string, string> requiredNamespaces,
            Dictionary<string, string> remaps,
            Dictionary<string, string> withTypes,
            Dictionary<string, string> withAttributes)
        {
            using ConstantsScraperImpl imp = new ConstantsScraperImpl();
            return imp.ScrapeConstants(enumJsonFiles, defaultNamespace, scraperOutputDir, constantsHeaderText, exclusionNames, traversedHeaderToNamespaceMap, requiredNamespaces, remaps, withTypes, withAttributes);
        }

        private class ConstantsScraperImpl : IDisposable
        {
            private static readonly Regex DefineRegex =
                new Regex(
                    @"^\s*#define\s+([_A-Z][\dA-Za-z_]+)\s+(.+)");

            private static readonly Regex DefineConstantRegex =
                new Regex(
                    @"^((_HRESULT_TYPEDEF_|_NDIS_ERROR_TYPEDEF_)\(((?:0x)?[\da-f]+L?)\)|(\(HRESULT\)((?:0x)?[\da-f]+L?))|(-?\d+\.\d+(?:e\+\d+)?f?)|((?:0x[\da-f]+|\-?\d+)(?:UL|L)?)|((\d+)\s*(<<\s*\d+))|(MAKEINTRESOURCE\(\s*(\-?\d+)\s*\))|(\(HWND\)(-?\d+))|([a-z0-9_]+\s*\+\s*(\d+|0x[0-de-f]+))|(\(NTSTATUS\)((?:0x)?[\da-f]+L?))|(\s*\(DWORD\)\s*\(?\s*-1(L|\b)\s*\)?)|(\(BCRYPT_ALG_HANDLE\)\s*((?:0x)?[\da-f]+L?))|([a-z0-9_]+))$", RegexOptions.IgnoreCase);

            private static readonly Regex DefineGuidConstRegex =
                new Regex(
                    @"^\s*(DEFINE_GUID|DEFINE_DEVPROPKEY|DEFINE_KNOWN_FOLDER|OUR_GUID_ENTRY)\s*\((.*)");

            private static readonly Regex DefineAviGuidConstRegex =
                new Regex(
                    @"^\s*(DEFINE_AVIGUID)\s*\(\s*(.*),\s*(.*),\s*(.*),\s*(.*)\s*\);");

            private static readonly Regex DefineMediaTypeGuidConstRegex =
                new Regex(
                    @"^\s*(DEFINE_MEDIATYPE_GUID)\s*\(\s*(\S+),\s*(\S+)\s*\);");

            private static readonly Regex FccRegex =
                new Regex(
                    @"FCC\(\'(.{4})\'\)");

            private static readonly Regex DefineEnumFlagsRegex =
                new Regex(
                    @"^\s*DEFINE_ENUM_FLAG_OPERATORS\(\s*(\S+)\s*\)\s*\;\s*$");

            private static readonly Regex CtlCodeRegex =
                new Regex(
                    @"^\s*CTL_CODE\((.+)\)");

            private static readonly Regex HidUsageRegex =
                new Regex(
                    @"^\s*\(USAGE\)\s*(0x[\da-f]+)", RegexOptions.IgnoreCase);

            private static readonly Regex MakeHresultRegex =
                new Regex(
                    @"^\s*(?:MAKE_HRESULT|MAKE_SCODE)\((.+)\)");

            private static readonly Regex NamePartsRegex = new Regex(@"[A-Z]+[a-z]*");

            private static readonly Regex ContainsLowerCase = new Regex(@"[a-z]+");

            private Dictionary<string, EnumWriter> namespacesToEnumWriters = new Dictionary<string, EnumWriter>();
            private Dictionary<string, ConstantWriter> namespacesToConstantWriters = new Dictionary<string, ConstantWriter>();
            private WildcardDictionary requiredNamespaces;
            private Dictionary<string, string> scannedNamesToNamespaces;
            private Dictionary<string, string> writtenConstants;

            private List<EnumObject> enumObjectsFromJsons;
            private Dictionary<string, string> withTypes;
            private Dictionary<string, string> withAttributes;

            private Dictionary<string, List<EnumObject>> enumMemberNameToEnumObj;
            private HashSet<string> exclusionNames;

            private string scraperOutputDir;
            private string constantsHeaderText;
            private string enumFlagsFixupFileName;
            private string defaultNamespace;

            private List<string> output = new List<string>();
            private List<string> suggestedEnumRenames = new List<string>();

            public ConstantsScraperImpl()
            {
            }

            public ScraperResults ScrapeConstants(
                string[] enumJsonFiles,
                string defaultNamespace,
                string scraperOutputDir,
                string constantsHeaderText,
                HashSet<string> exclusionNames,
                Dictionary<string, string> traversedHeaderToNamespaceMap,
                Dictionary<string, string> requiredNamespaces,
                Dictionary<string, string> remaps,
                Dictionary<string, string> withTypes,
                Dictionary<string, string> withAttributes)
            {
                this.requiredNamespaces = new WildcardDictionary(requiredNamespaces);
                this.withTypes = withTypes;
                this.exclusionNames = exclusionNames;
                this.constantsHeaderText = constantsHeaderText;
                this.withAttributes = withAttributes;
                this.scraperOutputDir = scraperOutputDir;
                this.defaultNamespace = defaultNamespace;

                this.scannedNamesToNamespaces = ScraperUtils.GetNameToNamespaceMap(scraperOutputDir);

                this.writtenConstants = ScraperUtils.GetConstants(scraperOutputDir);

                this.CleanExistingFiles();

                this.LoadEnumObjectsFromJsonFiles(enumJsonFiles);

                this.ScrapeConstantsFromTraversedFiles(traversedHeaderToNamespaceMap);

                this.WriteEnumsAndRemaps(remaps);

                return new ScraperResults(this.output);
            }

            public void Dispose()
            {
                foreach (EnumWriter enumWriter in this.namespacesToEnumWriters.Values)
                {
                    enumWriter.Dispose();
                }

                this.namespacesToEnumWriters.Clear();

                foreach (ConstantWriter constantWriter in this.namespacesToConstantWriters.Values)
                {
                    constantWriter.Dispose();
                }

                this.namespacesToConstantWriters.Clear();
            }

            private static Dictionary<string, string> GetAutoValueReplacements()
            {
                Dictionary<string, string> ret = new Dictionary<string, string>();
                ret["TRUE"] = "1";
                ret["FALSE"] = "0";

                return ret;
            }

            private static List<EnumObject> LoadEnumsFromSourceFiles(IEnumerable<string> fileNames)
            {
                List<EnumObject> enumObjects = new List<EnumObject>();

                foreach (var file in fileNames)
                {
                    enumObjects.AddRange(EnumObject.LoadFromFile(file));
                }

                return enumObjects;
            }

            private static string StripComments(string rawValue)
            {
                int commentIndex = rawValue.IndexOf("//");
                if (commentIndex != -1)
                {
                    rawValue = rawValue.Substring(0, commentIndex).Trim();
                }

                commentIndex = rawValue.IndexOf("/*");
                if (commentIndex != -1)
                {
                    rawValue = rawValue.Substring(0, commentIndex).Trim();
                }

                return rawValue;
            }

            private void CleanExistingFiles()
            {
                foreach (string file in Directory.GetFiles(this.scraperOutputDir).Where(f => f.EndsWith(".enums.cs") || f.EndsWith(".constants.cs")))
                {
                    File.Delete(file);
                }
            }

            private void InitEnumFlagsFixupFile()
            {
                if (this.enumFlagsFixupFileName == null)
                {
                    this.enumFlagsFixupFileName = Path.Combine(this.scraperOutputDir, "enumsMakeFlags.generated.rsp");
                    if (File.Exists(this.enumFlagsFixupFileName))
                    {
                        File.Delete(this.enumFlagsFixupFileName);
                    }

                    File.AppendAllText(this.enumFlagsFixupFileName, "--enumMakeFlags\r\n");
                }
            }

            private void LoadMemberNameToEnumObjMap(List<EnumObject> enumObjects)
            {
                this.enumMemberNameToEnumObj = new Dictionary<string, List<EnumObject>>();
                foreach (EnumObject obj in enumObjects)
                {
                    foreach (EnumObject.Member member in obj.members)
                    {
                        if (StringComparer.OrdinalIgnoreCase.Equals(member.name, "None"))
                        {
                            continue;
                        }

                        if (!this.enumMemberNameToEnumObj.TryGetValue(member.name, out var objList))
                        {
                            objList = new List<EnumObject>();
                            this.enumMemberNameToEnumObj[member.name] = objList;
                        }

                        objList.Add(obj);
                    }
                }
            }

            private List<EnumObject> LoadEnumsFromJsonFiles(string[] enumJsonFiles)
            {
                List<EnumObject> enumObjects = new List<EnumObject>();

                if (enumJsonFiles != null)
                {
                    foreach (var enumJsonFile in enumJsonFiles)
                    {
                        enumObjects.AddRange(EnumObject.LoadFromFile(enumJsonFile));
                    }
                }

                return enumObjects;
            }

            private string GetForcedTypeForName(string name)
            {
                // Make all error codes uint to match GetLastError even though they're defined as signed
                // in winerror.h
                if (name.StartsWith("ERROR_"))
                {
                    return "uint";
                }

                this.withTypes.TryGetValue(name, out string forceType);

                return forceType;
            }

            private void AddMakeHresultConstant(string originalNamespace, string name, string severity, string facility, string code)
            {
                string valueText = $"unchecked((int)(({severity}) << 31) | (((int)({facility})) << 16) | (int)({code}))";
                this.AddConstantInteger(originalNamespace, "HRESULT", name, valueText);
            }

            private void AddCtlCodeConstant(string originalNamespace, string name, string deviceType, string function, string method, string access)
            {
                if (this.writtenConstants.ContainsKey(name))
                {
                    return;
                }

                var writer = this.GetConstantWriter(originalNamespace, name);

                function = function.Replace("SCMBUS_FUNCTION(", "(IOCTL_SCMBUS_DEVICE_FUNCTION_BASE + ");
                function = function.Replace("SCM_LOGICAL_DEVICE_FUNCTION(", "(IOCTL_SCM_LOGICAL_DEVICE_FUNCTION_BASE + ");
                function = function.Replace("SCM_PHYSICAL_DEVICE_FUNCTION(", "(IOCTL_SCM_PHYSICAL_DEVICE_FUNCTION_BASE + ");

                writer.AddValue("uint", name, $"(({deviceType}) << 16) | (uint)(((int)({access})) << 14) | (({function}) << 2) | ({method})");

                this.writtenConstants.Add(name, "uint");
            }

            private void AddConstantValue(string originalNamespace, string type, string name, string valueText)
            {
                if (this.writtenConstants.ContainsKey(name))
                {
                    return;
                }

                var writer = this.GetConstantWriter(originalNamespace, name);
                writer.AddValue(type, name, valueText);

                this.writtenConstants.Add(name, type);
            }
            
            private void AddConstantGuid(string defineGuidKeyword, string originalNamespace, string line)
            {
                int firstComma = line.IndexOf(',');
                string name = line.Substring(0, firstComma).Trim();
                if (this.writtenConstants.ContainsKey(name))
                {
                    return;
                }

                if (this.ShouldExclude(name))
                {
                    return;
                }

                string args = line.Substring(firstComma + 1).Trim();
                int closeParen = args.IndexOf(')');
                args = args.Substring(0, closeParen);

                var writer = this.GetConstantWriter(originalNamespace, name);

                if (defineGuidKeyword == "DEFINE_DEVPROPKEY")
                {
                    writer.AddPropKey(name, args);
                }
                else
                {
                    writer.AddGuid(name, args);
                }

                this.writtenConstants.Add(name, "Guid");
            }

            private void AddConstantInteger(string originalNamespace, string nativeTypeName, string name, string valueText)
            {
                if (this.writtenConstants.ContainsKey(name))
                {
                    return;
                }

                string forcedType = nativeTypeName != null ? null : this.GetForcedTypeForName(name);

                var writer = this.GetConstantWriter(originalNamespace, name);
                writer.AddInt(forcedType, nativeTypeName, name, valueText, out var finalType);

                this.writtenConstants.Add(name, finalType);
            }

            private ConstantWriter GetConstantWriter(string originalNamespace, string name)
            {
                string foundNamespace = originalNamespace;

                string newNamespace = this.LookupNamespaceForName(name);
                if (!string.IsNullOrEmpty(newNamespace))
                {
                    foundNamespace = newNamespace;
                }

                if (!this.namespacesToConstantWriters.TryGetValue(foundNamespace, out ConstantWriter constantWriter))
                {
                    string partConstantsFile = Path.Combine(this.scraperOutputDir, $@"{foundNamespace}.constants.cs");
                    if (File.Exists(partConstantsFile))
                    {
                        File.Delete(partConstantsFile);
                    }

                    constantWriter = new ConstantWriter(partConstantsFile, foundNamespace, this.constantsHeaderText, this.withAttributes);
                    this.namespacesToConstantWriters.Add(foundNamespace, constantWriter);
                }

                return constantWriter;
            }

            private HashSet<string> GetManualEnumMemberNames()
            {
                List<EnumObject> enumObjectsFromManualSources = LoadEnumsFromSourceFiles(Directory.GetFiles(this.scraperOutputDir, "*.manual.cs"));
                HashSet<string> manualEnumMemberNames = new HashSet<string>();
                foreach (EnumObject obj in enumObjectsFromManualSources)
                {
                    foreach (EnumObject.Member member in obj.members)
                    {
                        manualEnumMemberNames.Add(member.name);
                    }
                }

                return manualEnumMemberNames;
            }

            private void LoadEnumObjectsFromJsonFiles(string[] enumJsonFiles)
            {
                // Load the enums scraped from the docs
                this.enumObjectsFromJsons = this.LoadEnumsFromJsonFiles(enumJsonFiles);

                // Load a map from member names to enum obj
                this.LoadMemberNameToEnumObjMap(this.enumObjectsFromJsons);
            }

            private bool ShouldExclude(string constName)
            {
                return this.exclusionNames.Contains(constName);
            }

            private void ScrapeConstantsFromTraversedFiles(Dictionary<string, string> traversedFileMap)
            {
                Dictionary<string, string> autoReplacements = GetAutoValueReplacements();

                HashSet<string> manualEnumMemberNames = this.GetManualEnumMemberNames();

                // For each traversed header, scrape the constants
                foreach (KeyValuePair<string, string> item in traversedFileMap)
                {
                    var header = item.Key;
                    var currentNamespace = item.Value;

                    if (!File.Exists(header))
                    {
                        continue;
                    }

                    string currentHeaderName = Path.GetFileName(header).ToLowerInvariant();

                    List<EnumObject> autoEnumObjsForCurrentHeader =
                        new List<EnumObject>(this.enumObjectsFromJsons.Where(e => e.autoPopulate != null && e.autoPopulate.header.ToLowerInvariant() == currentHeaderName));
                    Regex autoPopulateReg = null;

                    if (autoEnumObjsForCurrentHeader.Count != 0)
                    {
                        StringBuilder autoPopulateRegexPattern = new StringBuilder();
                        foreach (EnumObject autoEnumObj in autoEnumObjsForCurrentHeader)
                        {
                            if (autoPopulateRegexPattern.Length != 0)
                            {
                                autoPopulateRegexPattern.Append('|');
                            }

                            autoPopulateRegexPattern.Append($"(^{autoEnumObj.autoPopulate.filter})");
                        }

                        autoPopulateReg = new Regex(autoPopulateRegexPattern.ToString());
                    }

                    string continuation = null;
                    bool processingGuidMultiLine = false;
                    string defineGuidKeyword = null;
                    foreach (string currentLine in File.ReadAllLines(header))
                    {
                        string line = continuation == null ? currentLine : continuation + currentLine;
                        if (line.EndsWith("\\"))
                        {
                            continuation = line.Substring(0, line.Length - 1);
                            continue;
                        }

                        if (processingGuidMultiLine)
                        {
                            continuation = StripComments(line).Trim();
                            if (continuation.EndsWith(';'))
                            {
                                processingGuidMultiLine = false;
                                this.AddConstantGuid(defineGuidKeyword, currentNamespace, continuation);
                                continuation = null;
                            }

                            continue;
                        }

                        continuation = null;

                        Match defineGuidMatch = DefineGuidConstRegex.Match(line);
                        if (defineGuidMatch.Success)
                        {
                            defineGuidKeyword = defineGuidMatch.Groups[1].Value;
                            line = defineGuidMatch.Groups[2].Value;
                            line = StripComments(line).Trim();
                            if (line.EndsWith(';'))
                            {
                                this.AddConstantGuid(defineGuidKeyword, currentNamespace, line);
                            }
                            else
                            {
                                continuation = line;
                                processingGuidMultiLine = true;
                            }

                            continue;
                        }

                        Match defineAviGuidMatch = DefineAviGuidConstRegex.Match(line);
                        if (defineAviGuidMatch.Success)
                        {
                            defineGuidKeyword = defineAviGuidMatch.Groups[1].Value;
                            var guidName = defineAviGuidMatch.Groups[2].Value;
                            var l = defineAviGuidMatch.Groups[3].Value;
                            var w1 = defineAviGuidMatch.Groups[4].Value;
                            var w2 = defineAviGuidMatch.Groups[5].Value;
                            var defineGuidLine = $"{guidName}, {l}, {w1}, {w2}, 0xC0,0,0,0,0,0,0,0x46)";
                            this.AddConstantGuid(defineGuidKeyword, currentNamespace, defineGuidLine);
                            continue;
                        }

                        Match defineMediaTypeGuidMatch = DefineMediaTypeGuidConstRegex.Match(line);
                        if (defineMediaTypeGuidMatch.Success)
                        {
                            defineGuidKeyword = defineMediaTypeGuidMatch.Groups[1].Value;
                            var guidName = defineMediaTypeGuidMatch.Groups[2].Value;
                            var value = defineMediaTypeGuidMatch.Groups[3].Value;
                            var fccMatch = FccRegex.Match(value);
                            if (fccMatch.Success)
                            {
                                var fccValue = fccMatch.Groups[1].Value.ToArray();
                                uint convertedValue =
                                    (uint)(fccValue[0]) |
                                    (uint)(fccValue[1] << 8) |
                                    (uint)(fccValue[2] << 16) |
                                    (uint)(fccValue[3] << 24);
                                value = $"0x{convertedValue:x}";
                            }

                            var defineGuidLine = $"{guidName}, {value}, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71)";
                            this.AddConstantGuid(defineGuidKeyword, currentNamespace, defineGuidLine);
                            continue;
                        }

                        Match defineMatch = DefineRegex.Match(line);

                        // Skip if not #define ...
                        if (!defineMatch.Success)
                        {
                            this.TryScrapingEnumFlags(line);
                            continue;
                        }

                        string name = defineMatch.Groups[1].Value;
                        if (this.ShouldExclude(name))
                        {
                            continue;
                        }

                        // Get rid of trailing comments
                        string rawValue = StripComments(defineMatch.Groups[2].Value.Trim());

                        if (autoReplacements.TryGetValue(rawValue, out var updatedRawValue))
                        {
                            rawValue = updatedRawValue;
                        }

                        string fixedRawValue = rawValue;
                        // Get rid of enclosing parens. Makes it easier to parse with regex
                        if (fixedRawValue.StartsWith('(') && fixedRawValue.EndsWith(')'))
                        {
                            fixedRawValue = fixedRawValue.Substring(1, rawValue.Length - 2).Trim();
                        }

                        Match ctlCodeMatch = CtlCodeRegex.Match(fixedRawValue);
                        if (ctlCodeMatch.Success)
                        {
                            var parts = ctlCodeMatch.Groups[1].Value.Split(',');
                            if (parts.Length == 4)
                            {
                                this.AddCtlCodeConstant(currentNamespace, name, parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), parts[3].Trim());
                                continue;
                            }
                        }

                        Match usageMatch = HidUsageRegex.Match(fixedRawValue);
                        if (usageMatch.Success)
                        {
                            this.AddConstantValue(currentNamespace, "ushort", name, usageMatch.Groups[1].Value);
                            continue;
                        }

                        if (fixedRawValue.StartsWith("AUDCLNT_ERR("))
                        {
                            fixedRawValue = fixedRawValue.Replace("AUDCLNT_ERR(", "MAKE_HRESULT(SEVERITY_ERROR, FACILITY_AUDCLNT, ");
                        }
                        else if (fixedRawValue.StartsWith("AUDCLNT_SUCCESS("))
                        {
                            fixedRawValue = fixedRawValue.Replace("AUDCLNT_SUCCESS(", "MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_AUDCLNT, ");
                        }

                        Match makeHresultMatch = MakeHresultRegex.Match(fixedRawValue);
                        if (makeHresultMatch.Success)
                        {
                            var parts = makeHresultMatch.Groups[1].Value.Split(',');
                            if (parts.Length == 3)
                            {
                                this.AddMakeHresultConstant(currentNamespace, name, parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
                                continue;
                            }
                        }

                        // See if matches one of our well known constants formats
                        Match match = DefineConstantRegex.Match(fixedRawValue);
                        string valueText;
                        string nativeTypeName = null;
                        string matchedConstantType = null;
                        bool matchedToOtherName = false;

                        if (match.Success)
                        {
                            // #define E_UNEXPECTED _HRESULT_TYPEDEF_(0x8000FFFF)
                            if (!string.IsNullOrEmpty(match.Groups[2].Value))
                            {
                                if (match.Groups[2].Value == "_HRESULT_TYPEDEF_")
                                {
                                    nativeTypeName = "HRESULT";
                                }

                                valueText = match.Groups[3].Value;
                            }
                            // #define E_UNEXPECTED ((HRESULT)0x8000FFFF)
                            else if (!string.IsNullOrEmpty(match.Groups[5].Value))
                            {
                                nativeTypeName = "HRESULT";
                                valueText = match.Groups[5].Value;
                            }
                            // #define DXGI_RESOURCE_PRIORITY_MINIMUM	( 0x28000000 )
                            else if (!string.IsNullOrEmpty(match.Groups[7].Value))
                            {
                                valueText = match.Groups[7].Value;
                            }
                            // 1.0, -2.0f
                            else if (!string.IsNullOrEmpty(match.Groups[6].Value))
                            {
                                valueText = match.Groups[6].Value;
                                string type = valueText.EndsWith('f') ? "float" : "double";
                                this.AddConstantValue(currentNamespace, type, name, valueText);
                                continue;
                            }
                            // 1 << 5
                            else if (!string.IsNullOrEmpty(match.Groups[8].Value))
                            {
                                string part1 = match.Groups[9].Value + "u";
                                string part2 = match.Groups[10].Value;
                                valueText = part1 + part2;
                            }
                            // MAKEINTRESOURCE(-4)
                            else if (!string.IsNullOrEmpty(match.Groups[11].Value))
                            {
                                nativeTypeName = "LPCWSTR";
                                valueText = match.Groups[12].Value;
                                this.AddConstantInteger(currentNamespace, nativeTypeName, name, valueText);
                                continue;
                            }
                            // (HWND)-4
                            else if (!string.IsNullOrEmpty(match.Groups[13].Value))
                            {
                                nativeTypeName = "HWND";
                                valueText = match.Groups[14].Value;
                                this.AddConstantInteger(currentNamespace, nativeTypeName, name, valueText);
                                continue;
                            }
                            // (IDENT_FOO + 4)
                            else if (match.Groups[15].Success)
                            {
                                valueText = match.Groups[15].Value;
                            }
                            // (NTSTATUS)0x00000000L
                            else if (match.Groups[17].Success)
                            {
                                nativeTypeName = "NTSTATUS";
                                valueText = match.Groups[18].Value;
                            }
                            // (DWORD)-1
                            else if (match.Groups[20].Success)
                            {
                                valueText = "0xFFFFFFFF";
                            }
                            // (BCRYPT_ALG_HANDLE) 0x000001a1
                            else if (match.Groups[21].Success)
                            {
                                nativeTypeName = "BCRYPT_ALG_HANDLE";
                                valueText = match.Groups[22].Value;
                            }
                            // SOME_OTHER_CONSTANT
                            else if (match.Groups[23].Success)
                            {
                                string otherName = match.Groups[23].Value;

                                matchedToOtherName = true;

                                // Only use a constant as the value if we have seen the constant before
                                // and we know its type
                                if (this.writtenConstants.TryGetValue(otherName, out var otherType))
                                {
                                    // Skip guids for now
                                    if (otherType != "Guid")
                                    {
                                        matchedConstantType = otherType;
                                    }
                                }

                                // If we didn't match it to another constant, keep going as we may be setting an enum
                                valueText = otherName;
                            }
                            else
                            {
                                continue;
                            }
                        }
                        else
                        {
                            valueText = rawValue;

                            // Don't do anything with strings. They can't be part of enums
                            if (valueText.StartsWith('"') || valueText.StartsWith("L\"") || valueText.StartsWith("__TEXT"))
                            {
                                continue;
                            }

                            valueText = valueText.Replace("(DWORD)", "(uint)");
                            valueText = valueText.Replace("(ULONG)", "(uint)");
                        }

                        bool updatedEnum = false;

                        // If we see the member is part of an enum, update the member value
                        if (this.enumMemberNameToEnumObj.TryGetValue(name, out var enumObjList))
                        {
                            foreach (EnumObject enumObj in enumObjList)
                            {
                                enumObj.AddIfNotSet(name, valueText);
                                updatedEnum = true;
                            }
                        }

                        if (autoPopulateReg != null && nativeTypeName == null)
                        {
                            Match autoPopulate = autoPopulateReg.Match(name);
                            if (autoPopulate.Success)
                            {
                                for (int i = 1; i < autoPopulate.Groups.Count; i++)
                                {
                                    if (!string.IsNullOrEmpty(autoPopulate.Groups[i].Value))
                                    {
                                        EnumObject foundObjEnum = autoEnumObjsForCurrentHeader[i - 1];
                                        foundObjEnum.AddIfNotSet(name, valueText);
                                        updatedEnum = true;
                                        if (!this.enumMemberNameToEnumObj.TryGetValue(name, out var list))
                                        {
                                            list = new List<EnumObject>();
                                            this.enumMemberNameToEnumObj.Add(name, list);
                                        }

                                        list.Add(foundObjEnum);

                                        break;
                                    }
                                }
                            }
                        }

                        // If we haven't used the member to update an enum, skip it...
                        // ...unless it's an HRESULT. Always emit them as constants too
                        if (match.Success && (!updatedEnum || nativeTypeName != null))
                        {
                            // Only add the constant if it's not part of a manual enum
                            if (!manualEnumMemberNames.Contains(name))
                            {
                                if (matchedConstantType != null)
                                {
                                    this.AddConstantValue(currentNamespace, matchedConstantType, name, valueText);
                                }
                                else
                                {
                                    // Only add as an int if it didn't match some other constant/enum name
                                    if (!matchedToOtherName)
                                    {
                                        this.AddConstantInteger(currentNamespace, nativeTypeName, name, valueText);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            private void TryScrapingEnumFlags(string line)
            {
                Match match = DefineEnumFlagsRegex.Match(line);
                if (match.Success)
                {
                    var enumName = match.Groups[1].Value;

                    this.InitEnumFlagsFixupFile();
                    File.AppendAllText(this.enumFlagsFixupFileName, $"{enumName}\r\n");
                }
            }

            private void AddEnumWarningAndSuggestedMapping(string message, EnumObject enumObject)
            {
                this.output.Add(message);
                StringBuilder suggestedName = new StringBuilder();
                foreach (var match in NamePartsRegex.Matches(enumObject.name))
                {
                    if (suggestedName.Length != 0)
                    {
                        suggestedName.Append('_');
                    }

                    suggestedName.Append(match.ToString().ToUpperInvariant());
                }

                if (suggestedName.Length != 0)
                {
                    this.suggestedEnumRenames.Add($"{enumObject.name}={suggestedName}");
                }
            }

            private string LookupNamespaceForName(string name)
            {
                if (!this.requiredNamespaces.TryGetValue(name, out var foundNamespace))
                {
                    this.scannedNamesToNamespaces.TryGetValue(name, out foundNamespace);
                }

                // If it contains more than one, just return null
                if (foundNamespace != null && foundNamespace.Contains(';'))
                {
                    foundNamespace = null;
                }

                return foundNamespace;
            }

            private void WriteEnumsAndRemaps(
                Dictionary<string, string> remaps)
            {
                // Output the enums and the rsp entries that map parameters and fields to use
                // enum names
                var enumRemapsFileName = Path.Combine(this.scraperOutputDir, "enumsRemap.rsp");

                bool linesAdded = false;
                using (StreamWriter enumRemapsWriter = new StreamWriter(enumRemapsFileName))
                {
                    enumRemapsWriter.WriteLine("--memberRemap");

                    // For each enum object...
                    foreach (EnumObject obj in this.enumObjectsFromJsons)
                    {
                        string foundNamespace = obj.@namespace;
                        List<string> remapsToAdd = new List<string>();
                        int remapCount = 0;
                        // For each use in an enum...
                        foreach (EnumObject.Use use in obj.uses)
                        {
                            string lookupNameForNamespace;
                            string remapName = use.ToString();

                            // lookupNameForNamespace = the name to use to lookup the namespace
                            // remapName = used in .rsp to map a param or field to use the enum
                            if (use.@interface != null)
                            {
                                lookupNameForNamespace = use.@interface;
                            }
                            else if (use.method != null)
                            {
                                lookupNameForNamespace = use.method;
                            }
                            else
                            {
                                lookupNameForNamespace = use.@struct;
                            }

                            // If we haven't found a namespace yet, try to look it up
                            if (string.IsNullOrEmpty(foundNamespace))
                            {
                                foundNamespace = this.LookupNamespaceForName(lookupNameForNamespace);
                            }

                            // If we don't already have a remap entry for this param or field, add one
                            if (!remaps.TryGetValue(remapName, out string remapValue))
                            {
                                remapsToAdd.Add(remapName);
                                remapCount++;
                            }
                            else
                            {
                                // If the existing remap wants to use this enum, keep track
                                // so that we write the enum
                                if (remapValue == obj.name)
                                {
                                    remapCount++;
                                }
                            }
                        }

                        if (foundNamespace == null)
                        {
                            foundNamespace = "Windows.Win32.System.SystemServices";
                        }

                        bool addedEnum = false;

                        // If the enum doesn't have any remaps and isn't
                        // an auto-populate, go to next object
                        bool shouldWriteEnum = (remapCount != 0 || obj.autoPopulate != null);

                        if (!shouldWriteEnum)
                        {
                            continue;
                        }

                        // Lookup the enum writer in the cache or add it if we can't find it
                        if (!this.namespacesToEnumWriters.TryGetValue(foundNamespace, out var enumWriter))
                        {
                            string fixedNamespaceName = foundNamespace.Replace("Windows.Win32.", string.Empty);
                            string enumFile = Path.Combine(this.scraperOutputDir, $"{fixedNamespaceName}.enums.cs");
                            if (File.Exists(enumFile))
                            {
                                File.Delete(enumFile);
                            }

                            enumWriter = new EnumWriter(enumFile, foundNamespace, this.constantsHeaderText);
                            this.namespacesToEnumWriters.Add(foundNamespace, enumWriter);
                        }

                        if (obj.name != null)
                        {
                            if (this.writtenConstants.ContainsKey(obj.name))
                            {
                                throw new InvalidOperationException($"Tried to add enum {obj.name} but a constant with the same name already exists.");
                            }

                            addedEnum = enumWriter.AddEnum(obj);
                            if (addedEnum)
                            {
                                if (ContainsLowerCase.IsMatch(obj.name))
                                {
                                    this.AddEnumWarningAndSuggestedMapping(
                                        $"Warning: {obj.name} enum contains lower case. Consider giving it an intentional name in the enums json file.",
                                        obj);
                                }
                            }
                        }

                        if (addedEnum || obj.addUsesTo != null)
                        {
                            var enumName = obj.name ?? obj.addUsesTo;
                            foreach (var remap in remapsToAdd)
                            {
                                linesAdded = true;
                                enumRemapsWriter.WriteLine($"{remap}={enumName}");

                                remaps.TryAdd(remap, enumName);
                            }
                        }
                    }
                }

                if (!linesAdded)
                {
                    File.Delete(enumRemapsFileName);
                }

                if (this.suggestedEnumRenames.Count != 0)
                {
                    this.output.Add("Suggested enum names:");
                    this.output.AddRange(this.suggestedEnumRenames);
                    this.suggestedEnumRenames.Clear();
                }
            }
        }
    }

    public class ScraperResults
    {
        public ScraperResults(IEnumerable<string> results)
        {
            this.Output = results;
        }

        public IEnumerable<string> Output { get; }
    }
}
