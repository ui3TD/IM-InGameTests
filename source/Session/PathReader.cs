using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace InGameTests
{
    /// <summary>
    /// The loaded types by dotted name, with nested types written with dots too: data_girls.girls
    /// for data_girls+girls. A name matches a type's full name or its name without the namespace.
    /// </summary>
    internal sealed class TypeIndex
    {
        private readonly Dictionary<string, List<Type>> byName = new Dictionary<string, List<Type>>(StringComparer.Ordinal);

        public TypeIndex(IEnumerable<Assembly> assemblies)
        {
            foreach (Assembly assembly in assemblies)
            {
                foreach (Type type in SafeTypes(assembly))
                {
                    if (type.Name.IndexOf('<') >= 0)
                    {
                        continue;
                    }
                    string full = type.FullName?.Replace('+', '.');
                    if (full == null)
                    {
                        continue;
                    }
                    Add(full, type);
                    if (type.Namespace != null)
                    {
                        Add(full.Substring(type.Namespace.Length + 1), type);
                    }
                }
            }
        }

        /// <summary>Every loaded assembly except dynamic ones (Harmony's patch assemblies).</summary>
        public static TypeIndex OfLoadedAssemblies() =>
            new TypeIndex(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic));

        private void Add(string name, Type type)
        {
            if (!byName.TryGetValue(name, out List<Type> types))
            {
                byName[name] = types = new List<Type>();
            }
            if (!types.Contains(type))
            {
                types.Add(type);
            }
        }

        /// <summary>The type with this name, null if none; throws if several assemblies have one.</summary>
        public Type Find(string name)
        {
            if (!byName.TryGetValue(name, out List<Type> types))
            {
                return null;
            }
            if (types.Count > 1)
            {
                throw new ArgumentException("'" + name + "' names " + types.Count + " types: "
                    + string.Join(", ", types.Select(t => t.FullName + " in " + t.Assembly.GetName().Name).ToArray())
                    + "; write it with its namespace");
            }
            return types[0];
        }

        /// <summary>
        /// Splits "a.b.c.rest" at the longest prefix that names a type. Returns the type and the
        /// number of dotted parts it took; throws if no prefix names one.
        /// </summary>
        public Type FindPrefix(IList<string> parts, out int used)
        {
            for (used = parts.Count; used >= 1; used--)
            {
                Type type = Find(string.Join(".", parts.Take(used).ToArray()));
                if (type != null)
                {
                    return type;
                }
            }
            throw new ArgumentException("No loaded type is named " + parts[0]
                + (parts.Count > 1 ? " (or " + string.Join(".", parts.Take(2).ToArray()) + "...)" : "")
                + "; names are case-sensitive, and nested types are written with dots");
        }

        /// <summary>The methods "Type.Method" names: every overload declared on that type. Throws if there are none.</summary>
        public List<MethodInfo> Methods(string typeAndMethod)
        {
            string[] parts = typeAndMethod.Split('.');
            if (parts.Length < 2)
            {
                throw new ArgumentException("Write the method as Type.Method, e.g. mainScript.Time_SetState");
            }
            Type type = Find(string.Join(".", parts.Take(parts.Length - 1).ToArray()));
            if (type == null)
            {
                throw new ArgumentException("No loaded type is named " + string.Join(".", parts.Take(parts.Length - 1).ToArray())
                    + "; names are case-sensitive, and nested types are written with dots");
            }
            string name = parts[parts.Length - 1];
            List<MethodInfo> methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                                                       | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters).ToList();
            if (methods.Count == 0)
            {
                throw new ArgumentException(type.FullName + " declares no method " + name
                    + " with a body (properties are get_Name and set_Name; inherited methods are on their base type)");
            }
            return methods;
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null);
            }
            catch (Exception)
            {
                return Enumerable.Empty<Type>();
            }
        }
    }

    /// <summary>
    /// Reads a value by path, read-only: a type, then fields and properties, with [i] for a list
    /// index or dictionary key, [member=value] for the items whose member reads as value, and [*]
    /// for every item. Examples: staticVars.dateTime, data_girls.girl[id=3].fans, data_girls.girl[*].id.
    /// It never calls methods; property getters are the only code it runs.
    /// </summary>
    internal static class PathReader
    {
        private const int MaxLines = 200;

        private sealed class Step
        {
            public string Member;
            public readonly List<string> Selectors = new List<string>();
        }

        /// <summary>The value at the path, as text: one value in full, or one line per value a [*] or [member=value] gave.</summary>
        public static string Read(TypeIndex types, string path)
        {
            List<Step> steps = ParsePath(path);
            List<string> names = steps.Select(s => s.Member).ToList();
            // The type is the longest leading run of plain names (no selectors before the last one).
            int plain = steps.TakeWhile((s, i) => i == 0 || steps[i - 1].Selectors.Count == 0).Count();
            Type root = types.FindPrefix(names.Take(plain).ToList(), out int used);
            if (steps[used - 1].Selectors.Count > 0)
            {
                throw new ArgumentException("A type can't take [" + steps[used - 1].Selectors[0] + "]; select on one of its members");
            }

            var values = new List<KeyValuePair<string, object>>();
            bool many = false;
            if (used == steps.Count)
            {
                return root.FullName + " (a type; name one of its static members)";
            }
            values.Add(new KeyValuePair<string, object>("", null));
            for (int i = used; i < steps.Count; i++)
            {
                Step step = steps[i];
                var next = new List<KeyValuePair<string, object>>();
                foreach (var pair in values)
                {
                    object value = i == used ? ReadStatic(root, step.Member) : ReadMember(pair.Value, step.Member, pair.Key);
                    var selected = new List<KeyValuePair<string, object>> { new KeyValuePair<string, object>(pair.Key, value) };
                    foreach (string selector in step.Selectors)
                    {
                        many |= selector == "*" || selector.IndexOf('=') > 0;
                        selected = selected.SelectMany(p => Select(p.Value, selector, p.Key)).ToList();
                    }
                    next.AddRange(selected);
                }
                values = next;
            }

            if (!many)
            {
                return ValueText.Full(values[0].Value);
            }
            if (values.Count == 1)
            {
                return "1 value: " + values[0].Key + "\n" + ValueText.Full(values[0].Value);
            }
            var sb = new StringBuilder();
            sb.Append(values.Count.ToString(CultureInfo.InvariantCulture)).Append(values.Count == 1 ? " value" : " values");
            foreach (var pair in values.Take(MaxLines))
            {
                sb.Append('\n').Append(pair.Key).Append(" = ").Append(ValueText.Short(pair.Value));
            }
            if (values.Count > MaxLines)
            {
                sb.Append("\n... ").Append(values.Count - MaxLines).Append(" more");
            }
            return sb.ToString();
        }

        private static List<Step> ParsePath(string path)
        {
            var steps = new List<Step>();
            int i = 0;
            while (true)
            {
                int start = i;
                while (i < path.Length && path[i] != '.' && path[i] != '[')
                {
                    i++;
                }
                var step = new Step { Member = path.Substring(start, i - start) };
                if (step.Member.Length == 0)
                {
                    throw new ArgumentException("Path '" + path + "' has an empty name at character " + (start + 1));
                }
                while (i < path.Length && path[i] == '[')
                {
                    int close = path.IndexOf(']', i);
                    if (close < 0)
                    {
                        throw new ArgumentException("Path '" + path + "' has a [ that isn't closed");
                    }
                    step.Selectors.Add(path.Substring(i + 1, close - i - 1));
                    i = close + 1;
                }
                steps.Add(step);
                if (i == path.Length)
                {
                    return steps;
                }
                if (path[i] != '.')
                {
                    throw new ArgumentException("Path '" + path + "': expected . or [ at character " + (i + 1));
                }
                i++;
            }
        }

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        private static object ReadStatic(Type type, string member)
        {
            FieldInfo field = type.GetField(member, Any | BindingFlags.Static);
            if (field != null)
            {
                return field.GetValue(null);
            }
            PropertyInfo property = type.GetProperty(member, Any | BindingFlags.Static);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return Get(property, null);
            }
            throw new ArgumentException(type.FullName + " has no static field or property " + member);
        }

        private static object ReadMember(object target, string member, string label)
        {
            if (target == null)
            {
                throw new ArgumentException((label.Length > 0 ? label + ": " : "") + "can't read " + member + " of null");
            }
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(member, Any | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field.GetValue(field.IsStatic ? null : target);
                }
                PropertyInfo property = type.GetProperty(member, Any | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return Get(property, property.GetGetMethod(true)?.IsStatic == true ? null : target);
                }
            }
            throw new ArgumentException((label.Length > 0 ? label + ": " : "") + target.GetType().FullName + " has no field or property " + member);
        }

        private static object Get(PropertyInfo property, object target)
        {
            if (!property.CanRead)
            {
                throw new ArgumentException(property.DeclaringType.FullName + "." + property.Name + " has no getter");
            }
            try
            {
                return property.GetValue(target, null);
            }
            catch (TargetInvocationException ex)
            {
                throw new ArgumentException(property.DeclaringType.FullName + "." + property.Name + " threw " + ex.InnerException);
            }
        }

        private static IEnumerable<KeyValuePair<string, object>> Select(object value, string selector, string label)
        {
            string where = label.Length > 0 ? label + ": " : "";
            if (value == null)
            {
                throw new ArgumentException(where + "can't take [" + selector + "] of null");
            }
            if (selector == "*" || selector.IndexOf('=') > 0)
            {
                if (!(value is IEnumerable items) || value is string)
                {
                    throw new ArgumentException(where + "[" + selector + "] needs a list, got " + value.GetType().FullName);
                }
                int eq = selector.IndexOf('=');
                string member = eq > 0 ? selector.Substring(0, eq) : null;
                string wanted = eq > 0 ? selector.Substring(eq + 1) : null;
                var result = new List<KeyValuePair<string, object>>();
                int i = 0;
                foreach (object item in Items(value, items))
                {
                    string itemLabel = label + "[" + (value is IDictionary ? ValueText.Short(((DictionaryEntry)item).Key) : i.ToString(CultureInfo.InvariantCulture)) + "]";
                    object element = value is IDictionary ? ((DictionaryEntry)item).Value : item;
                    if (member == null || (element != null && ValueText.Short(ReadMember(element, member, itemLabel)) == wanted))
                    {
                        result.Add(new KeyValuePair<string, object>(itemLabel, element));
                    }
                    i++;
                }
                return result;
            }
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (ValueText.Short(entry.Key) == selector)
                    {
                        return new[] { new KeyValuePair<string, object>(label + "[" + selector + "]", entry.Value) };
                    }
                }
                throw new ArgumentException(where + "no key " + selector + " among " + dictionary.Count + " entries");
            }
            if (value is IList list)
            {
                if (!int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                {
                    throw new ArgumentException(where + "[" + selector + "]: a list takes a number, [member=value] or [*]");
                }
                if (index < 0 || index >= list.Count)
                {
                    throw new ArgumentException(where + "index " + index + " is outside the list's " + list.Count + " items");
                }
                return new[] { new KeyValuePair<string, object>(label + "[" + selector + "]", list[index]) };
            }
            throw new ArgumentException(where + "[" + selector + "] needs a list or dictionary, got " + value.GetType().FullName);
        }

        /// <summary>A dictionary's entries as DictionaryEntry (its plain enumerator gives KeyValuePairs), else the items.</summary>
        private static IEnumerable Items(object value, IEnumerable items)
        {
            if (!(value is IDictionary dictionary))
            {
                return items;
            }
            var entries = new List<DictionaryEntry>();
            foreach (DictionaryEntry entry in dictionary)
            {
                entries.Add(entry);
            }
            return entries;
        }
    }

    /// <summary>Values as text, for session replies and call logs. Invariant culture; never throws.</summary>
    internal static class ValueText
    {
        private const int MaxText = 120;
        private const int MaxItems = 50;
        private const int MaxFields = 100;

        /// <summary>One line: a simple value as itself, a list as its count, anything else as its type.</summary>
        public static string Short(object value)
        {
            try
            {
                switch (value)
                {
                    case null:
                        return "null";
                    case string s:
                        return s.Length > MaxText ? s.Substring(0, MaxText) + "..." : s;
                    case DateTime d:
                        return d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    case IFormattable f when value.GetType().IsPrimitive || value is decimal || value is Enum:
                        return f.ToString(null, CultureInfo.InvariantCulture);
                    case bool b:
                        return b ? "True" : "False";
                    case char c:
                        return c.ToString();
                    case ICollection collection:
                        return "(" + collection.Count + (collection.Count == 1 ? " item)" : " items)");
                    default:
                        return "(" + TypeName(value.GetType()) + ")";
                }
            }
            catch (Exception ex)
            {
                return "(unreadable: " + ex.GetType().Name + ")";
            }
        }

        /// <summary>
        /// Several lines: a list's items, a dictionary's entries or an object's fields, one level
        /// deep. Objects show fields only, since some properties (in Unity especially) make copies.
        /// </summary>
        public static string Full(object value)
        {
            if (value == null || IsSimple(value))
            {
                return Short(value);
            }
            var sb = new StringBuilder();
            try
            {
                if (value is IDictionary dictionary)
                {
                    sb.Append(Short(value));
                    int i = 0;
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (i++ == MaxItems)
                        {
                            sb.Append("\n...");
                            break;
                        }
                        sb.Append("\n[").Append(Short(entry.Key)).Append("] = ").Append(Short(entry.Value));
                    }
                }
                else if (value is IEnumerable items)
                {
                    int i = 0;
                    var lines = new StringBuilder();
                    foreach (object item in items)
                    {
                        if (i < MaxItems)
                        {
                            lines.Append("\n[").Append(i).Append("] = ").Append(Short(item));
                        }
                        i++;
                    }
                    sb.Append("(").Append(i).Append(i == 1 ? " item)" : " items)").Append(lines);
                    if (i > MaxItems)
                    {
                        sb.Append("\n...");
                    }
                }
                else
                {
                    sb.Append("(").Append(TypeName(value.GetType())).Append(")");
                    int n = 0;
                    for (Type type = value.GetType(); type != null && type != typeof(object); type = type.BaseType)
                    {
                        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (field.Name.IndexOf('<') >= 0 && !field.Name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                            {
                                continue;
                            }
                            if (n++ == MaxFields)
                            {
                                sb.Append("\n...");
                                return sb.ToString();
                            }
                            string name = field.Name.EndsWith(">k__BackingField", StringComparison.Ordinal)
                                ? field.Name.Substring(1, field.Name.IndexOf('>') - 1) : field.Name;
                            sb.Append('\n').Append(name).Append(" = ").Append(Short(ReadField(field, value)));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.Append("\n(stopped: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append(")");
            }
            return sb.ToString();
        }

        private static object ReadField(FieldInfo field, object target)
        {
            try
            {
                return field.GetValue(target);
            }
            catch (Exception ex)
            {
                return "(unreadable: " + ex.GetType().Name + ")";
            }
        }

        private static bool IsSimple(object value) =>
            value is string || value is DateTime || value is decimal || value is Enum || value.GetType().IsPrimitive;

        /// <summary>C#-style name: List&lt;girls&gt;, data_girls.girls.</summary>
        public static string TypeName(Type type)
        {
            string name = type.IsNested ? TypeName(type.DeclaringType) + "." + type.Name : type.Name;
            if (!type.IsGenericType)
            {
                return name;
            }
            int tick = name.IndexOf('`');
            return (tick >= 0 ? name.Substring(0, tick) : name)
                   + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName).ToArray()) + ">";
        }
    }
}
