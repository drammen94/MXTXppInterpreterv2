using System;
using System.Collections.Generic;
using System.Linq;
using XppInterpreter.Interpreter.Bytecode;
using XppInterpreter.Interpreter.Proxy;
using XppInterpreter.Parser;

namespace XppInterpreter.Interpreter.Metadata
{
    public enum DynamicModelKind
    {
        ScriptClass,
        Class,
        Table,
        Form,
        DataEntity,
        Report,
        Service,
        Enum,
        View,
        Map,
    }

    public sealed class DynamicModelFieldDescriptor
    {
        public string Name { get; set; }
        public string TypeName { get; set; }
        public Type ClrType { get; set; }
        public bool IsStatic { get; set; }
        public IList<XppAttributeDeclaration> Attributes { get; } = new List<XppAttributeDeclaration>();
    }

    public sealed class DynamicModelMethodDescriptor
    {
        public string Name { get; set; }
        public string ReturnTypeName { get; set; }
        public bool IsStatic { get; set; }
        public int ParameterCount { get; set; }
        public IList<XppAttributeDeclaration> Attributes { get; } = new List<XppAttributeDeclaration>();
    }

    /// <summary>
    /// Runtime model overlay. Native-backed entries point at real AOS metadata;
    /// script entries provide non-persistent definitions that hosts can project as
    /// forms, entities, services or temporary tables without installing a model.
    /// </summary>
    public sealed class DynamicModelDescriptor
    {
        public string Name { get; set; }
        public DynamicModelKind Kind { get; set; }
        public bool IsNativeBacked { get; set; }
        public bool IsPersistent { get; set; }
        public string NativeTypeName { get; set; }
        public string RuntimeClassName { get; set; }
        public string BackingModelName { get; set; }
        public IList<XppAttributeDeclaration> Attributes { get; } = new List<XppAttributeDeclaration>();
        public IList<DynamicModelFieldDescriptor> Fields { get; } = new List<DynamicModelFieldDescriptor>();
        public IList<DynamicModelMethodDescriptor> Methods { get; } = new List<DynamicModelMethodDescriptor>();
    }

    public sealed class DynamicModelRegistry
    {
        private readonly Dictionary<string, DynamicModelDescriptor> _models =
            new Dictionary<string, DynamicModelDescriptor>(StringComparer.OrdinalIgnoreCase);
        private readonly XppProxy _proxy;
        private readonly AosTypeDescriptorProvider _aosTypes;
        private readonly RuntimeContext _context;
        private readonly Dictionary<string, List<RuntimeRecordSnapshot>> _runtimeRows =
            new Dictionary<string, List<RuntimeRecordSnapshot>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _runtimeRecIds =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly List<RuntimeStoreSnapshot> _transactionSnapshots = new List<RuntimeStoreSnapshot>();

        public DynamicModelRegistry()
        {
        }

        public DynamicModelRegistry(XppProxy proxy)
        {
            _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
            _aosTypes = new AosTypeDescriptorProvider(proxy);
        }

        public DynamicModelRegistry(RuntimeContext context)
            : this(context?.Proxy ?? throw new ArgumentNullException(nameof(context)))
        {
            _context = context;
        }

        public IEnumerable<DynamicModelDescriptor> All => _models.Values;

        public DynamicModelDescriptor Find(string name)
        {
            if (name is null) return null;
            if (_models.TryGetValue(name, out DynamicModelDescriptor descriptor)) return descriptor;

            AosTypeDescriptor native = _aosTypes?.DescribeType(name);
            if (native is null) return null;

            descriptor = new DynamicModelDescriptor
            {
                Name = native.Name,
                NativeTypeName = native.Name,
                Kind = ToDynamicKind(native.Kind),
                IsNativeBacked = true,
                IsPersistent = native.Kind == AosTypeKind.Table || native.Kind == AosTypeKind.DataEntity,
            };
            Register(descriptor);
            return descriptor;
        }

        /// <summary>Create a real AOS class instance or Common table/entity buffer.</summary>
        public object Create(string name, params object[] arguments)
        {
            DynamicModelDescriptor descriptor = Find(name);
            if (descriptor is null)
            {
                throw new ArgumentException($"Runtime or AOS metadata artifact {name} was not found.", nameof(name));
            }

            if (!descriptor.IsNativeBacked && !string.IsNullOrEmpty(descriptor.RuntimeClassName))
            {
                EnsureRuntimeContext();
                RuntimeClass runtimeClass = _context.ByteCode.FindDeclaredClass(descriptor.RuntimeClassName);
                if (runtimeClass is null)
                {
                    throw new ArgumentException($"Runtime implementation class {descriptor.RuntimeClassName} was not found in the active bytecode.", nameof(name));
                }

                return ScriptClassRuntime.Instantiate(_context, runtimeClass, arguments ?? new object[0]);
            }

            EnsureNative(name);

            if (descriptor.Kind == DynamicModelKind.Table || descriptor.Kind == DynamicModelKind.DataEntity)
            {
                if (arguments != null && arguments.Length != 0)
                {
                    throw new ArgumentException("A table or entity buffer does not take constructor arguments.", nameof(arguments));
                }

                return _proxy.Casting.GetDefaultValueForType(name);
            }

            if (descriptor.Kind != DynamicModelKind.Class && descriptor.Kind != DynamicModelKind.Service)
            {
                throw new InvalidOperationException($"A {descriptor.Kind} metadata artifact is not directly constructible; invoke its AOS controller/menu API from X++.");
            }

            return _proxy.Reflection.CreateInstance(name, arguments ?? new object[0]);
        }

        public object InvokeInstance(object instance, string methodName, params object[] arguments)
        {
            if (instance is null) throw new ArgumentNullException(nameof(instance));
            arguments = arguments ?? new object[0];

            if (instance is ScriptObject scriptObject)
            {
                EnsureRuntimeContext();
                RuntimeClassMethod method = scriptObject.Class.FindDispatchMethod(methodName, isStatic: false);
                if (method is null) throw new MissingMethodException(scriptObject.Class.Name, methodName);

                ScriptClassRuntime.Invocation invocation = ScriptClassRuntime.RunSynchronous(
                    _context, method, scriptObject, arguments);
                CopyUpdatedArguments(invocation.UpdatedArguments, arguments);
                return invocation.ReturnValue;
            }

            if (_context != null)
            {
                AosCallResult dispatched = AosCallDispatcher.CallInstance(_context, instance, methodName, arguments);
                CopyUpdatedArguments(dispatched.UpdatedArguments, arguments);
                return dispatched.ReturnValue;
            }

            return _proxy.Reflection.CallInstanceFunction(instance, methodName, arguments);
        }

        public object InvokeStatic(string typeName, string methodName, params object[] arguments)
        {
            arguments = arguments ?? new object[0];
            DynamicModelDescriptor descriptor = Find(typeName);

            if (descriptor?.Kind == DynamicModelKind.ScriptClass)
            {
                EnsureRuntimeContext();
                RuntimeClass runtimeClass = _context.ByteCode.FindDeclaredClass(typeName);
                RuntimeClassMethod method = runtimeClass?.FindDispatchMethod(methodName, isStatic: true);
                if (method is null) throw new MissingMethodException(typeName, methodName);

                ScriptClassRuntime.Invocation invocation = ScriptClassRuntime.RunSynchronous(
                    _context, method, null, arguments);
                CopyUpdatedArguments(invocation.UpdatedArguments, arguments);
                return invocation.ReturnValue;
            }

            EnsureNative(typeName);
            if (_context != null)
            {
                AosCallResult dispatched = AosCallDispatcher.CallStatic(_context, typeName, methodName, arguments);
                CopyUpdatedArguments(dispatched.UpdatedArguments, arguments);
                return dispatched.ReturnValue;
            }

            return _proxy.Reflection.CallStaticFunction(typeName, methodName, arguments);
        }

        public object GetField(object instance, string fieldName)
        {
            if (instance is null) throw new ArgumentNullException(nameof(instance));

            if (instance is ScriptObject scriptObject)
            {
                RuntimeClassField field = RequireExternalInstanceField(scriptObject, fieldName);
                return scriptObject.FieldScope.GetVar(field.Name);
            }

            return _proxy.Reflection.GetInstanceProperty(instance, fieldName);
        }

        public void SetField(object instance, string fieldName, object value)
        {
            if (instance is null) throw new ArgumentNullException(nameof(instance));

            if (instance is ScriptObject scriptObject)
            {
                RuntimeClassField field = RequireExternalInstanceField(scriptObject, fieldName);
                if (field.IsConst)
                {
                    throw new InvalidOperationException($"Cannot assign to const field {field.DeclaringClass.Name}.{field.Name}.");
                }

                scriptObject.FieldScope.SetVar(field.Name, value, _proxy.Casting);
                return;
            }

            _proxy.Reflection.SetInstanceProperty(instance, fieldName, value);
        }

        public object GetStaticField(string typeName, string fieldName)
        {
            RuntimeClass runtimeClass = _context?.ByteCode.FindDeclaredClass(typeName);
            if (runtimeClass != null)
            {
                RuntimeClassField field = RequireExternalStaticField(runtimeClass, fieldName);
                return ScriptClassRuntime.EnsureStaticsInitialized(_context, field.DeclaringClass).GetVar(field.Name);
            }

            EnsureNative(typeName);
            return _proxy.Reflection.GetStaticProperty(typeName, fieldName);
        }

        public void SetStaticField(string typeName, string fieldName, object value)
        {
            RuntimeClass runtimeClass = _context?.ByteCode.FindDeclaredClass(typeName);
            if (runtimeClass != null)
            {
                RuntimeClassField field = RequireExternalStaticField(runtimeClass, fieldName);
                if (field.IsConst)
                {
                    throw new InvalidOperationException($"Cannot assign to const field {field.DeclaringClass.Name}.{field.Name}.");
                }

                ScriptClassRuntime.EnsureStaticsInitialized(_context, field.DeclaringClass)
                    .SetVar(field.Name, value, _proxy.Casting);
                return;
            }

            EnsureNative(typeName);
            _proxy.Reflection.SetStaticProperty(typeName, fieldName, value);
        }

        public void Register(DynamicModelDescriptor descriptor)
        {
            if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.Name))
            {
                throw new ArgumentException("A runtime model must have a name.", nameof(descriptor));
            }

            _models[descriptor.Name] = descriptor;
        }

        public bool IsRuntimeTable(string name)
        {
            DynamicModelDescriptor descriptor = Find(name);
            return descriptor != null && !descriptor.IsNativeBacked &&
                (descriptor.Kind == DynamicModelKind.Table || descriptor.Kind == DynamicModelKind.DataEntity ||
                 descriptor.Kind == DynamicModelKind.View);
        }

        public bool IsRuntimeTableBuffer(object value)
        {
            return value is ScriptObject scriptObject && IsRuntimeTable(scriptObject.Class.Name);
        }

        internal IReadOnlyList<RuntimeRecordSnapshot> GetRuntimeRows(string modelName)
        {
            EnsureRuntimeTable(modelName);
            string storageName = RuntimeStorageName(modelName);
            if (!_runtimeRows.TryGetValue(storageName, out List<RuntimeRecordSnapshot> rows))
            {
                rows = new List<RuntimeRecordSnapshot>();
                _runtimeRows[storageName] = rows;
            }
            return rows;
        }

        internal RuntimeRecordSnapshot CaptureRecord(ScriptObject buffer)
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (RuntimeClassField field in EnumerateFields(buffer.Class))
            {
                values[field.Name] = buffer.FieldScope.GetVar(field.Name);
            }
            return new RuntimeRecordSnapshot(values);
        }

        internal void ApplyRecord(RuntimeRecordSnapshot record, ScriptObject buffer)
        {
            if (record is null || buffer is null) return;
            foreach (RuntimeClassField field in EnumerateFields(buffer.Class))
            {
                if (record.Values.TryGetValue(field.Name, out object value))
                {
                    buffer.FieldScope.SetVar(field.Name, value, _proxy.Casting);
                }
            }
        }

        internal void ReplaceRuntimeRows(string modelName, IEnumerable<RuntimeRecordSnapshot> rows)
        {
            EnsureRuntimeTable(modelName);
            _runtimeRows[RuntimeStorageName(modelName)] = (rows ?? Enumerable.Empty<RuntimeRecordSnapshot>())
                .Select(row => row.Clone()).ToList();
        }

        public void InsertRecord(ScriptObject buffer)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            string modelName = buffer.Class.Name;
            EnsureRuntimeTable(modelName);
            EnsureWritable(modelName);
            AssignRecId(buffer, RuntimeStorageName(modelName));
            RuntimeRecordSnapshot record = CaptureRecord(buffer);

            if (FindRecordIndex(modelName, record) >= 0)
            {
                throw new InvalidOperationException($"A {modelName} record with the same runtime key already exists.");
            }

            RuntimeRows(modelName).Add(record);
        }

        public void UpdateRecord(ScriptObject buffer)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            string modelName = buffer.Class.Name;
            EnsureRuntimeTable(modelName);
            EnsureWritable(modelName);
            RuntimeRecordSnapshot record = CaptureRecord(buffer);
            int index = FindRecordIndex(modelName, record);
            if (index < 0) throw new InvalidOperationException($"The {modelName} record was not found for update.");
            RuntimeRows(modelName)[index] = record;
        }

        public void DeleteRecord(ScriptObject buffer)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            string modelName = buffer.Class.Name;
            EnsureRuntimeTable(modelName);
            EnsureWritable(modelName);
            int index = FindRecordIndex(modelName, CaptureRecord(buffer));
            if (index >= 0) RuntimeRows(modelName).RemoveAt(index);
        }

        public bool RereadRecord(ScriptObject buffer)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            string modelName = buffer.Class.Name;
            EnsureRuntimeTable(modelName);
            int index = FindRecordIndex(modelName, CaptureRecord(buffer));
            if (index < 0) return false;
            ApplyRecord(RuntimeRows(modelName)[index], buffer);
            return true;
        }

        public void ClearRecord(ScriptObject buffer)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            foreach (RuntimeClassField field in EnumerateFields(buffer.Class))
            {
                object value = field.ClrType != null
                    ? _proxy.Casting.GetDefaultValueForSystemType(field.ClrType)
                    : _proxy.Casting.GetDefaultValueForType(field.TypeName);
                buffer.FieldScope.SetVar(field.Name, value, _proxy.Casting);
            }
        }

        public bool TryInvokeRuntimeRecordMethod(ScriptObject buffer, string methodName, object[] arguments, out object result)
        {
            result = null;
            if (!IsRuntimeTableBuffer(buffer) || buffer.Class.FindDispatchMethod(methodName, isStatic: false) != null)
            {
                return false;
            }

            if ((arguments?.Length ?? 0) != 0) return false;
            switch ((methodName ?? string.Empty).ToLowerInvariant())
            {
                case "insert":
                case "doinsert": InsertRecord(buffer); return true;
                case "update":
                case "doupdate": UpdateRecord(buffer); return true;
                case "delete":
                case "dodelete": DeleteRecord(buffer); return true;
                case "write":
                    if (FindRecordIndex(buffer.Class.Name, CaptureRecord(buffer)) >= 0) UpdateRecord(buffer);
                    else InsertRecord(buffer);
                    return true;
                case "reread": result = RereadRecord(buffer); return true;
                case "clear": ClearRecord(buffer); return true;
                default: return false;
            }
        }

        public void BeginTransaction()
        {
            _transactionSnapshots.Add(RuntimeStoreSnapshot.Capture(_runtimeRows, _runtimeRecIds));
        }

        public void CommitTransaction()
        {
            if (_transactionSnapshots.Count > 0)
            {
                _transactionSnapshots.RemoveAt(_transactionSnapshots.Count - 1);
            }
        }

        public void AbortTransaction()
        {
            if (_transactionSnapshots.Count == 0) return;
            RuntimeStoreSnapshot original = _transactionSnapshots[0];
            original.Restore(_runtimeRows, _runtimeRecIds);
            _transactionSnapshots.Clear();
        }

        internal void Synchronize(IEnumerable<RuntimeClass> runtimeClasses)
        {
            foreach (RuntimeClass runtimeClass in runtimeClasses ?? Enumerable.Empty<RuntimeClass>())
            {
                if (Find(runtimeClass.Name) is null)
                {
                    DynamicModelKind kind = RuntimeKind(runtimeClass, out string backingModelName);
                    var descriptor = new DynamicModelDescriptor
                    {
                        Name = runtimeClass.Name,
                        Kind = kind,
                        RuntimeClassName = runtimeClass.Name,
                        BackingModelName = backingModelName,
                    };
                    foreach (XppAttributeDeclaration attribute in runtimeClass.Attributes)
                    {
                        descriptor.Attributes.Add(attribute);
                    }

                    foreach (RuntimeClassField field in runtimeClass.Fields)
                    {
                        var fieldDescriptor = new DynamicModelFieldDescriptor
                        {
                            Name = field.Name,
                            TypeName = field.TypeName,
                            ClrType = field.ClrType,
                            IsStatic = field.IsStatic,
                        };
                        foreach (XppAttributeDeclaration attribute in field.Attributes)
                        {
                            fieldDescriptor.Attributes.Add(attribute);
                        }
                        descriptor.Fields.Add(fieldDescriptor);
                    }

                    foreach (RuntimeClassMethod method in runtimeClass.Methods)
                    {
                        var methodDescriptor = new DynamicModelMethodDescriptor
                        {
                            Name = method.Name,
                            ReturnTypeName = method.ReturnType?.Lexeme,
                            IsStatic = method.IsStatic,
                            ParameterCount = method.Parameters.Count,
                        };
                        foreach (XppAttributeDeclaration attribute in method.Attributes)
                        {
                            methodDescriptor.Attributes.Add(attribute);
                        }
                        descriptor.Methods.Add(methodDescriptor);
                    }

                    Register(descriptor);
                }

                if (runtimeClass.HasCompiledExtensionTarget && Find(runtimeClass.ExtensionTargetName) is null)
                {
                    DynamicModelKind kind = runtimeClass.CompiledExtensionTargetKind == AosTypeKind.Table
                        ? DynamicModelKind.Table
                        : DynamicModelKind.Class;

                    Register(new DynamicModelDescriptor
                    {
                        Name = runtimeClass.ExtensionTargetName,
                        Kind = kind,
                        IsNativeBacked = true,
                        IsPersistent = kind == DynamicModelKind.Table || kind == DynamicModelKind.DataEntity,
                        NativeTypeName = runtimeClass.ExtensionTargetName,
                    });
                }
            }
        }

        private void EnsureNative(string name)
        {
            if (_proxy is null)
            {
                throw new InvalidOperationException("This registry is not bound to an AOS proxy. Use RuntimeContext.Models for live metadata and invocation.");
            }

            if (Find(name) is null)
            {
                throw new ArgumentException($"AOS metadata artifact {name} was not found.", nameof(name));
            }
        }

        private void EnsureRuntimeContext()
        {
            if (_context is null)
            {
                throw new InvalidOperationException("This registry has no active RuntimeContext. Use RuntimeContext.Models for script execution.");
            }
        }

        private void EnsureRuntimeTable(string modelName)
        {
            EnsureRuntimeContext();
            if (!IsRuntimeTable(modelName))
            {
                throw new InvalidOperationException($"Runtime model {modelName} is not a table, view, or data entity.");
            }
        }

        private void EnsureWritable(string modelName)
        {
            DynamicModelDescriptor descriptor = Find(modelName);
            if (descriptor?.Kind == DynamicModelKind.View)
            {
                throw new InvalidOperationException($"Runtime view {modelName} is read-only.");
            }
        }

        private List<RuntimeRecordSnapshot> RuntimeRows(string modelName)
        {
            return (List<RuntimeRecordSnapshot>)GetRuntimeRows(modelName);
        }

        private string RuntimeStorageName(string modelName)
        {
            DynamicModelDescriptor descriptor = Find(modelName);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (descriptor != null && !string.IsNullOrEmpty(descriptor.BackingModelName) &&
                   visited.Add(descriptor.Name))
            {
                DynamicModelDescriptor backing = Find(descriptor.BackingModelName);
                if (backing is null || backing.IsNativeBacked ||
                    (backing.Kind != DynamicModelKind.Table && backing.Kind != DynamicModelKind.DataEntity && backing.Kind != DynamicModelKind.View))
                {
                    break;
                }
                descriptor = backing;
            }
            return descriptor?.Name ?? modelName;
        }

        private void AssignRecId(ScriptObject buffer, string modelName)
        {
            RuntimeClassField recId = EnumerateFields(buffer.Class)
                .FirstOrDefault(field => string.Equals(field.Name, "RecId", StringComparison.OrdinalIgnoreCase));
            if (recId is null) return;

            object current = buffer.FieldScope.GetVar(recId.Name);
            if (Convert.ToInt64(current ?? 0) != 0) return;
            long next = _runtimeRecIds.TryGetValue(modelName, out long last) ? last + 1 : 1;
            _runtimeRecIds[modelName] = next;
            buffer.FieldScope.SetVar(recId.Name, next, _proxy.Casting);
        }

        private int FindRecordIndex(string modelName, RuntimeRecordSnapshot candidate)
        {
            List<RuntimeRecordSnapshot> rows = RuntimeRows(modelName);
            List<DynamicModelFieldDescriptor> keys = KeyFields(Find(modelName)).ToList();
            for (int index = 0; index < rows.Count; index++)
            {
                if (keys.All(key => Equals(rows[index].Value(key.Name), candidate.Value(key.Name)))) return index;
            }
            return -1;
        }

        private static IEnumerable<DynamicModelFieldDescriptor> KeyFields(DynamicModelDescriptor descriptor)
        {
            List<DynamicModelFieldDescriptor> explicitKeys = descriptor.Fields
                .Where(field => field.Attributes.Any(attribute => IsAttribute(attribute, "RuntimeKey")))
                .ToList();
            if (explicitKeys.Count > 0) return explicitKeys;
            DynamicModelFieldDescriptor recId = descriptor.Fields.FirstOrDefault(field =>
                string.Equals(field.Name, "RecId", StringComparison.OrdinalIgnoreCase));
            return recId != null ? new[] { recId } : descriptor.Fields.Where(field => !field.IsStatic).Take(1);
        }

        private static IEnumerable<RuntimeClassField> EnumerateFields(RuntimeClass runtimeClass)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (RuntimeClass current = runtimeClass; current != null; current = current.BaseClass)
            {
                foreach (RuntimeClassField field in current.Fields.Where(field => !field.IsStatic))
                {
                    if (seen.Add(field.Name)) yield return field;
                }
            }
        }

        private static DynamicModelKind RuntimeKind(RuntimeClass runtimeClass, out string backingModelName)
        {
            backingModelName = null;
            foreach (XppAttributeDeclaration attribute in runtimeClass.Attributes)
            {
                string name = NormalizeAttributeName(attribute.Name);
                DynamicModelKind? kind = null;
                switch (name.ToLowerInvariant())
                {
                    case "runtimetable": kind = DynamicModelKind.Table; break;
                    case "runtimedataentity": kind = DynamicModelKind.DataEntity; break;
                    case "runtimeform": kind = DynamicModelKind.Form; break;
                    case "runtimereport": kind = DynamicModelKind.Report; break;
                    case "runtimeservice": kind = DynamicModelKind.Service; break;
                    case "runtimeview": kind = DynamicModelKind.View; break;
                    case "runtimemap": kind = DynamicModelKind.Map; break;
                    case "runtimemodel":
                        if (attribute.Words.Count > 1 && Enum.TryParse(attribute.Words[1], true, out DynamicModelKind parsed)) kind = parsed;
                        break;
                }

                if (kind.HasValue)
                {
                    if (attribute.Words.Count > 1 && !string.Equals(name, "RuntimeModel", StringComparison.OrdinalIgnoreCase))
                    {
                        backingModelName = attribute.Words[1];
                    }
                    else if (attribute.Words.Count > 2)
                    {
                        backingModelName = attribute.Words[2];
                    }
                    return kind.Value;
                }
            }
            return DynamicModelKind.ScriptClass;
        }

        internal static bool IsAttribute(XppAttributeDeclaration attribute, string name)
        {
            return string.Equals(NormalizeAttributeName(attribute?.Name), name, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAttributeName(string name)
        {
            const string suffix = "Attribute";
            return name != null && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - suffix.Length)
                : name ?? string.Empty;
        }

        private static RuntimeClassField RequireExternalInstanceField(ScriptObject instance, string fieldName)
        {
            RuntimeClassField field = instance.Class.FindField(fieldName) ??
                instance.Class.FindExtensionField(fieldName, isStatic: false);
            if (field is null || field.IsStatic)
            {
                throw new MissingFieldException(instance.Class.Name, fieldName);
            }
            if (field.Access != AccessModifier.Public)
            {
                throw new MemberAccessException($"Field {field.DeclaringClass.Name}.{field.Name} is not public.");
            }
            return field;
        }

        private static RuntimeClassField RequireExternalStaticField(RuntimeClass runtimeClass, string fieldName)
        {
            RuntimeClassField field = runtimeClass.FindField(fieldName) ??
                runtimeClass.FindExtensionField(fieldName, isStatic: true);
            if (field is null || !field.IsStatic)
            {
                throw new MissingFieldException(runtimeClass.Name, fieldName);
            }
            if (field.Access != AccessModifier.Public)
            {
                throw new MemberAccessException($"Field {field.DeclaringClass.Name}.{field.Name} is not public.");
            }
            return field;
        }

        private static void CopyUpdatedArguments(object[] source, object[] destination)
        {
            if (source is null || destination is null) return;
            Array.Copy(source, destination, Math.Min(source.Length, destination.Length));
        }

        private static DynamicModelKind ToDynamicKind(AosTypeKind kind)
        {
            switch (kind)
            {
                case AosTypeKind.Table: return DynamicModelKind.Table;
                case AosTypeKind.Form: return DynamicModelKind.Form;
                case AosTypeKind.DataEntity: return DynamicModelKind.DataEntity;
                case AosTypeKind.Report: return DynamicModelKind.Report;
                case AosTypeKind.Service: return DynamicModelKind.Service;
                case AosTypeKind.Enum: return DynamicModelKind.Enum;
                default: return DynamicModelKind.Class;
            }
        }
    }

    internal sealed class RuntimeRecordSnapshot
    {
        public RuntimeRecordSnapshot(IDictionary<string, object> values)
        {
            Values = new Dictionary<string, object>(values ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
        }
        public Dictionary<string, object> Values { get; }
        public object Value(string name) => Values.TryGetValue(name, out object value) ? value : null;
        public RuntimeRecordSnapshot Clone() => new RuntimeRecordSnapshot(Values);
    }

    internal sealed class RuntimeStoreSnapshot
    {
        private readonly Dictionary<string, List<RuntimeRecordSnapshot>> _rows;
        private readonly Dictionary<string, long> _recIds;

        private RuntimeStoreSnapshot(Dictionary<string, List<RuntimeRecordSnapshot>> rows, Dictionary<string, long> recIds)
        {
            _rows = rows;
            _recIds = recIds;
        }

        public static RuntimeStoreSnapshot Capture(
            IDictionary<string, List<RuntimeRecordSnapshot>> rows,
            IDictionary<string, long> recIds)
        {
            return new RuntimeStoreSnapshot(
                rows.ToDictionary(pair => pair.Key, pair => pair.Value.Select(row => row.Clone()).ToList(), StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, long>(recIds, StringComparer.OrdinalIgnoreCase));
        }

        public void Restore(
            IDictionary<string, List<RuntimeRecordSnapshot>> rows,
            IDictionary<string, long> recIds)
        {
            rows.Clear();
            foreach (var pair in _rows) rows[pair.Key] = pair.Value.Select(row => row.Clone()).ToList();
            recIds.Clear();
            foreach (var pair in _recIds) recIds[pair.Key] = pair.Value;
        }
    }
}
