﻿using Common.Config;
using JiraExport.RevisionUtils;
using Migration.Common;
using Migration.Common.Log;
using Migration.WIContract;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace JiraExport
{
    public static class FieldMapperUtils
    {
        public static object MapRemainingWork(string seconds)
        {
            if (seconds == null)
                throw new ArgumentNullException(nameof(seconds));

            var secs = Convert.ToDouble(seconds);
            return TimeSpan.FromSeconds(secs).TotalHours;
        }

        public static (bool, object) MapTitle(JiraRevision r)
        {
            if (r == null)
                throw new ArgumentNullException(nameof(r));

            if (r.Fields.TryGetValue("summary", out object summary))
                return (true, $"[{r.ParentItem.Key}] {summary}");
            else
                return (false, null);
        }
        public static (bool, object) MapTitleWithoutKey(JiraRevision r)
        {
            if (r == null)
                throw new ArgumentNullException(nameof(r));

            if (r.Fields.TryGetValue("summary", out object summary))
                return (true, summary);
            else
                return (false, null);
        }

        public static (bool, object) MapValue(JiraRevision r, string itemSource, string itemTarget, ConfigJson config)
        {
            if (r == null)
                throw new ArgumentNullException(nameof(r));

            if (config == null)
                throw new ArgumentNullException(nameof(config));

            var targetWit = (from t in config.TypeMap.Types where t.Source == r.Type select t.Target).FirstOrDefault();

            var hasFieldValue = r.Fields.TryGetValue(itemSource, out object value);

            if (!hasFieldValue)
                return (false, null);

            foreach (var item in config.FieldMap.Fields)
            {
                if ((((item.Source == itemSource && item.Target == itemTarget) && (item.For.Contains(targetWit) || item.For == "All")) ||
                      item.Source == itemSource && (!string.IsNullOrWhiteSpace(item.NotFor) && !item.NotFor.Contains(targetWit))) &&
                      item.Mapping?.Values != null)
                {
                    var mappedValue = (from s in item.Mapping.Values where s.Source == value.ToString() select s.Target).FirstOrDefault();
                    if (string.IsNullOrEmpty(mappedValue))
                    {
                        Logger.Log(LogLevel.Warning, $"Missing mapping value '{value}' for field '{itemSource}' for item type '{r.Type}'.");
                    }
                    return (true, mappedValue);
                }
            }
            return (true, value);

        }

        public static (bool, object) MapRenderedValue(JiraRevision r, string sourceField, bool isCustomField, string customFieldName, ConfigJson config)
        {
            if (r == null)
                throw new ArgumentNullException(nameof(r));

            if (config == null)
                throw new ArgumentNullException(nameof(config));

            sourceField = SetCustomFieldName(sourceField, isCustomField, customFieldName);

            var fieldName = sourceField + "$Rendered";

            var targetWit = (from t in config.TypeMap.Types where t.Source == r.Type select t.Target).FirstOrDefault();

            var hasFieldValue = r.Fields.TryGetValue(fieldName, out object value);
            if (!hasFieldValue)
                return (false, null);

            foreach (var item in config.FieldMap.Fields)
            {
                if (((item.Source == fieldName && (item.For.Contains(targetWit) || item.For == "All")) ||
                      item.Source == fieldName && (!string.IsNullOrWhiteSpace(item.NotFor) && !item.NotFor.Contains(targetWit))) &&
                      item.Mapping?.Values != null)
                {
                    var mappedValue = (from s in item.Mapping.Values where s.Source == value.ToString() select s.Target).FirstOrDefault();
                    if (string.IsNullOrEmpty(mappedValue))
                    {
                        Logger.Log(LogLevel.Warning, $"Missing mapping value '{value}' for field '{fieldName}'.");
                    }
                    return (true, mappedValue);
                }
            }
            value = CorrectRenderedHtmlvalue(value, r);

            return (true, value);
        }



        public static object MapTags(string labels)
        {
            if (labels == null)
                throw new ArgumentNullException(nameof(labels));

            if (string.IsNullOrWhiteSpace(labels))
                return string.Empty;

            var tags = labels.Split(' ');
            if (!tags.Any())
                return string.Empty;
            else
                return string.Join(";", tags);
        }

        public static object MapArray(string field)
        {
            if (field == null)
                throw new ArgumentNullException(nameof(field));

            if (string.IsNullOrWhiteSpace(field))
                return null;

            var values = field.Split(',');
            if (!values.Any())
                return null;
            else
                return string.Join(";", values);
        }

        public static object MapSprint(string iterationPathsString)
        {
            if (string.IsNullOrWhiteSpace(iterationPathsString))
                return null;

            var iterationPaths = iterationPathsString.Split(',').AsEnumerable();
            iterationPaths = iterationPaths.Select(ip => ip.Trim());

            var iterationPath = iterationPaths.Last();

            return iterationPath;
        }

        private static readonly Dictionary<string, decimal> CalculatedLexoRanks = new Dictionary<string, decimal>();
        private static readonly Dictionary<decimal, string> CalculatedRanks = new Dictionary<decimal, string>();

        private static readonly Regex LexoRankRegex = new Regex(@"^[0-2]\|[0-9a-zA-Z]*(\:[0-9a-zA-Z]*)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

        public static object MapLexoRank(string lexoRank)
        {
            if (string.IsNullOrEmpty(lexoRank) || !LexoRankRegex.IsMatch(lexoRank))
                return decimal.MaxValue;

            if (CalculatedLexoRanks.ContainsKey(lexoRank))
            {
                Logger.Log(LogLevel.Warning, "Duplicate rank detected. You may need to re-balance the JIRA LexoRank. see: https://confluence.atlassian.com/adminjiraserver/managing-lexorank-938847803.html");
                return CalculatedLexoRanks[lexoRank];
            }

            // split by bucket and sub-rank delimiters
            var lexoSplit = lexoRank.Split(new[] {'|', ':'}, StringSplitOptions.RemoveEmptyEntries);

            // calculate the numeric value of the rank and sub-rank (if available)
            var b36Rank = Base36.Decode(lexoSplit[1]);
            var b36SubRank = lexoSplit.Length == 3 && !string.IsNullOrEmpty(lexoSplit[2])
                ? Base36.Decode(lexoSplit[2])
                : 0L;

            // calculate final rank value
            var rank = Convert.ToDecimal($"{b36Rank}.{b36SubRank}", CultureInfo.InvariantCulture.NumberFormat);

            if (CalculatedRanks.ContainsKey(rank) && CalculatedRanks[rank] != lexoRank)
                Logger.Log(LogLevel.Warning, "Duplicate rank detected for different LexoRank values. You may need to re-balance the JIRA LexoRank. see: https://confluence.atlassian.com/adminjiraserver/managing-lexorank-938847803.html");

            CalculatedRanks.Add(rank, lexoRank);
            CalculatedLexoRanks.Add(lexoRank, rank);
            return rank;
        }

        public static string CorrectRenderedHtmlvalue(object value, JiraRevision revision)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (revision == null)
                throw new ArgumentNullException(nameof(revision));

            var htmlValue = value.ToString();

            if (string.IsNullOrWhiteSpace(htmlValue))
                return htmlValue;

            foreach (var att in revision.AttachmentActions.Where(aa => aa.ChangeType == RevisionChangeType.Added).Select(aa => aa.Value))
            {
                if (!string.IsNullOrWhiteSpace(att.Url) && htmlValue.Contains(att.Url))
                    htmlValue = htmlValue.Replace(att.Url, att.Url);
            }

            htmlValue = RevisionUtility.ReplaceHtmlElements(htmlValue);

            string css = ReadEmbeddedFile("JiraExport.jirastyles.css");
            if (string.IsNullOrWhiteSpace(css))
                Logger.Log(LogLevel.Warning, $"Could not read css styles for rendered field in {revision.OriginId}.");
            else
                htmlValue = "<style>" + css + "</style>" + htmlValue;

            return htmlValue;
        }

        private static string ReadEmbeddedFile(string resourceName)
        {
            var assembly = Assembly.GetEntryAssembly();

            try
            {
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (ArgumentNullException)
            {
                return "";
            }
        }
        private static string SetCustomFieldName(string sourceField, bool isCustomField, string customFieldName)
        {
            if (isCustomField)
            {
                sourceField = customFieldName;
            }

            return sourceField;
        }
    }

}