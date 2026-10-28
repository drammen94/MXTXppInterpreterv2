using System;
using System.Collections.Generic;
using System.Linq;
using XppInterpreter.Interpreter.Bytecode;
using XppInterpreter.Interpreter.Metadata;
using XppInterpreter.Parser;
using XppInterpreter.Parser.Data;

namespace XppInterpreter.Interpreter.Query
{
    /// <summary>
    /// Routes each statement by buffer identity. Native Common buffers retain the
    /// SysDa implementation; interpreted runtime-table buffers use the in-process
    /// relational engine below. Mixed programs can use both in the same execution.
    /// </summary>
    internal static class QueryGeneratorRouter
    {
        public static IQueryGenerator ForQuery(RuntimeContext context, Parser.Data.Query query, object suppliedBuffer = null)
        {
            object buffer = suppliedBuffer ?? TryGetBuffer(context, query.TableVariableName);
            return context.Models.IsRuntimeTableBuffer(buffer) || context.Models.IsRuntimeTable(query.TableVariableName)
                ? (IQueryGenerator)new RuntimeModelQueryGenerator(context.Models)
                : context.Proxy.QueryGeneration.NewQueryGenerator();
        }

        public static IQueryGenerator ForMutation(RuntimeContext context, string targetBufferName)
        {
            object buffer = TryGetBuffer(context, targetBufferName);
            return context.Models.IsRuntimeTableBuffer(buffer)
                ? (IQueryGenerator)new RuntimeModelQueryGenerator(context.Models)
                : context.Proxy.QueryGeneration.NewQueryGenerator();
        }

        public static IQueryGenerator ForInsert(RuntimeContext context, InsertRecordset insert)
        {
            object target = TryGetBuffer(context, insert.TableVariableName);
            object source = TryGetBuffer(context, insert.Select.Query.TableVariableName);
            return context.Models.IsRuntimeTableBuffer(target) || context.Models.IsRuntimeTableBuffer(source)
                ? (IQueryGenerator)new RuntimeModelQueryGenerator(context.Models)
                : context.Proxy.QueryGeneration.NewQueryGenerator();
        }

        private static object TryGetBuffer(RuntimeContext context, string name)
        {
            try { return context.ScopeHandler.GetVar(name); }
            catch { return null; }
        }
    }

    internal sealed class RuntimeModelQueryGenerator : IQueryGenerator
    {
        private readonly DynamicModelRegistry _models;

        public RuntimeModelQueryGenerator(DynamicModelRegistry models)
        {
            _models = models ?? throw new ArgumentNullException(nameof(models));
        }

        public ISearchInstance NewSearchInstance(Parser.Data.Query query, RuntimeContext runtimeContext, bool isFromExpression, object buffer)
        {
            return new RuntimeModelSearchInstance(_models, runtimeContext, query, buffer as ScriptObject);
        }

        public void ExecuteDeleteFrom(DeleteFrom deleteFrom, RuntimeContext runtimeContext)
        {
            ScriptObject buffer = RequireBuffer(runtimeContext, deleteFrom.TableVariableName);
            var query = new Parser.Data.Query(deleteFrom.TableVariableName)
            {
                Where = deleteFrom.Where,
                Join = deleteFrom.Join,
            };
            ISearchInstance search = NewSearchInstance(query, runtimeContext, false, null);
            var deleted = new HashSet<string>(StringComparer.Ordinal);
            while (search.Next())
            {
                string identity = RecordIdentity(_models.CaptureRecord(buffer));
                if (deleted.Add(identity)) _models.DeleteRecord(buffer);
            }
            _models.ClearRecord(buffer);
        }

        public void ExecuteUpdateRecordset(UpdateRecordset updateRecordset, RuntimeContext runtimeContext)
        {
            ScriptObject buffer = RequireBuffer(runtimeContext, updateRecordset.TableVariableName);
            var helper = new QueryGenerationHelper(runtimeContext);
            var query = new Parser.Data.Query(updateRecordset.TableVariableName)
            {
                Where = updateRecordset.Where,
                Join = updateRecordset.Join,
            };
            ISearchInstance search = NewSearchInstance(query, runtimeContext, false, null);
            var updated = new HashSet<string>(StringComparer.Ordinal);
            while (search.Next())
            {
                string identity = RecordIdentity(_models.CaptureRecord(buffer));
                if (!updated.Add(identity)) continue;
                foreach (Setting setting in updateRecordset.Settings)
                {
                    object value = helper.ComputeExpression(setting.Expression);
                    _models.SetField(buffer, setting.FieldName, value);
                }
                _models.UpdateRecord(buffer);
            }
        }

        public void ExecuteInsertRecordset(InsertRecordset insertRecordset, RuntimeContext runtimeContext)
        {
            ScriptObject target = RequireBuffer(runtimeContext, insertRecordset.TableVariableName);
            IQueryGenerator sourceGenerator = QueryGeneratorRouter.ForQuery(runtimeContext, insertRecordset.Select.Query);
            ISearchInstance source = sourceGenerator.NewSearchInstance(insertRecordset.Select.Query, runtimeContext, false, null);
            IList<SelectionField> selected = insertRecordset.Select.Query.SelectionFields ?? new List<SelectionField>();

            if (selected.Count != insertRecordset.FieldList.Count)
            {
                throw new InvalidOperationException("insert_recordset requires one selected source field for each target field in the runtime model engine.");
            }

            while (source.Next())
            {
                _models.ClearRecord(target);
                for (int index = 0; index < selected.Count; index++)
                {
                    SelectionField selection = selected[index];
                    string sourceBufferName = selection.Field.TableVarName ?? insertRecordset.Select.Query.TableVariableName;
                    ScriptObject sourceBuffer = RequireBuffer(runtimeContext, sourceBufferName);
                    object value = _models.GetField(sourceBuffer, selection.Field.Name);
                    _models.SetField(target, insertRecordset.FieldList[index], value);
                }
                _models.InsertRecord(target);
            }
        }

        private static ScriptObject RequireBuffer(RuntimeContext context, string name)
        {
            object value = context.ScopeHandler.GetVar(name);
            if (!(value is ScriptObject buffer) || !context.Models.IsRuntimeTableBuffer(buffer))
            {
                throw new InvalidOperationException($"{name} is not an interpreted runtime-table buffer.");
            }
            return buffer;
        }

        private static string RecordIdentity(RuntimeRecordSnapshot record)
        {
            return string.Join("\u001f", record.Values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    internal sealed class RuntimeModelSearchInstance : ISearchInstance
    {
        private readonly DynamicModelRegistry _models;
        private readonly RuntimeContext _context;
        private readonly Parser.Data.Query _query;
        private readonly Dictionary<string, ScriptObject> _buffers =
            new Dictionary<string, ScriptObject>(StringComparer.OrdinalIgnoreCase);
        private readonly List<RuntimeQueryResult> _results;
        private int _index = -1;

        public RuntimeModelSearchInstance(
            DynamicModelRegistry models,
            RuntimeContext context,
            Parser.Data.Query query,
            ScriptObject suppliedBuffer)
        {
            _models = models;
            _context = context;
            _query = query;
            ResolveBuffers(query, suppliedBuffer);
            _results = BuildQuery(query, new[] { new RuntimeQueryResult() }).ToList();
            _results = ApplyGroupingAndOrdering(_results);

            int limit = query.FirstOnly ? 1 : query.FirstOnly10 ? 10 : query.FirstOnly100 ? 100 : query.FirstOnly1000 ? 1000 : int.MaxValue;
            if (query.NoFetch || query.GenerateOnly) _results.Clear();
            else if (_results.Count > limit) _results.RemoveRange(limit, _results.Count - limit);
        }

        public bool Next()
        {
            _index++;
            if (_index >= _results.Count) return false;
            Apply(_results[_index]);
            return true;
        }

        public void SetSearchObjects(object searchObject, object searchStatement) { }
        public bool IsNextExecuted() => _index >= 0;

        private IEnumerable<RuntimeQueryResult> BuildQuery(Parser.Data.Query query, IEnumerable<RuntimeQueryResult> seeds)
        {
            var matches = new List<RuntimeQueryResult>();
            ScriptObject buffer = _buffers[query.TableVariableName];
            foreach (RuntimeQueryResult seed in seeds)
            {
                Apply(seed);
                foreach (RuntimeRecordSnapshot row in _models.GetRuntimeRows(buffer.Class.Name))
                {
                    RuntimeQueryResult current = seed.With(query.TableVariableName, row);
                    Apply(current);
                    if (!Matches(query)) continue;
                    if (!MatchesCompanyAndValidity(query, row)) continue;

                    if (query.Join is null)
                    {
                        matches.Add(current);
                        continue;
                    }

                    List<RuntimeQueryResult> joined = BuildQuery(query.Join.Select, new[] { current }).ToList();
                    switch (query.Join.JoinType)
                    {
                        case JoinType.Regular:
                            matches.AddRange(joined);
                            break;
                        case JoinType.Exists:
                            if (joined.Count > 0) matches.Add(current);
                            break;
                        case JoinType.NotExists:
                            if (joined.Count == 0) matches.Add(current);
                            break;
                        case JoinType.Outer:
                            if (joined.Count > 0) matches.AddRange(joined);
                            else matches.Add(current.With(query.Join.Select.TableVariableName, EmptyRow(query.Join.Select.TableVariableName)));
                            break;
                    }
                }
            }
            return matches;
        }

        private List<RuntimeQueryResult> ApplyGroupingAndOrdering(List<RuntimeQueryResult> source)
        {
            IEnumerable<RuntimeQueryResult> results = source;
            bool hasAggregates = _query.SelectionFields?.Any(field => field.AggregateFunction != AggregateFunction.None) ?? false;
            if ((_query.GroupFields?.Count ?? 0) > 0 || hasAggregates)
            {
                IEnumerable<IGrouping<string, RuntimeQueryResult>> groups = results.GroupBy(result => GroupKey(result, _query.GroupFields));
                results = groups.Select(AggregateGroup);
            }

            IOrderedEnumerable<RuntimeQueryResult> ordered = null;
            foreach (OrderByField field in _query.OrderFields ?? Enumerable.Empty<OrderByField>())
            {
                Func<RuntimeQueryResult, object> key = result => result.Value(field.TableVarName ?? _query.TableVariableName, field.Name);
                bool descending = field.OrderByDirection == OrderByType.Descending;
                ordered = ordered is null
                    ? (descending ? results.OrderByDescending(key, RuntimeValueComparer.Instance) : results.OrderBy(key, RuntimeValueComparer.Instance))
                    : (descending ? ordered.ThenByDescending(key, RuntimeValueComparer.Instance) : ordered.ThenBy(key, RuntimeValueComparer.Instance));
            }
            if (ordered != null) results = ordered;
            if (_query.Reverse) results = results.Reverse();
            return results.ToList();
        }

        private RuntimeQueryResult AggregateGroup(IGrouping<string, RuntimeQueryResult> group)
        {
            RuntimeQueryResult result = group.First().Clone();
            foreach (SelectionField selection in _query.SelectionFields ?? Enumerable.Empty<SelectionField>())
            {
                if (selection.AggregateFunction == AggregateFunction.None) continue;
                string bufferName = selection.Field.TableVarName ?? _query.TableVariableName;
                List<object> values = group.Select(item => item.Value(bufferName, selection.Field.Name)).ToList();
                object aggregate;
                switch (selection.AggregateFunction)
                {
                    case AggregateFunction.Count: aggregate = values.Count(value => value != null); break;
                    case AggregateFunction.Sum: aggregate = values.Where(value => value != null).Sum(value => Convert.ToDecimal(value)); break;
                    case AggregateFunction.Avg:
                        List<decimal> decimals = values.Where(value => value != null).Select(Convert.ToDecimal).ToList();
                        aggregate = decimals.Count == 0 ? 0m : decimals.Average();
                        break;
                    case AggregateFunction.MinOf: aggregate = values.OrderBy(value => value, RuntimeValueComparer.Instance).FirstOrDefault(); break;
                    case AggregateFunction.MaxOf: aggregate = values.OrderByDescending(value => value, RuntimeValueComparer.Instance).FirstOrDefault(); break;
                    default: continue;
                }
                result.Rows[bufferName].Values[selection.Field.Name] = aggregate;
            }
            return result;
        }

        private string GroupKey(RuntimeQueryResult result, IList<Field> fields)
        {
            if (fields is null || fields.Count == 0) return "__all";
            return string.Join("\u001f", fields.Select(field => Convert.ToString(
                result.Value(field.TableVarName ?? _query.TableVariableName, field.Name),
                System.Globalization.CultureInfo.InvariantCulture)));
        }

        private bool Matches(Parser.Data.Query query)
        {
            if (query.Where is null) return true;
            object value = Evaluate(query.Where.Expression);
            return _context.Proxy.Casting.ToBoolean(value);
        }

        private object Evaluate(Expression expression)
        {
            if (expression is TableField tableField)
            {
                return _models.GetField(_buffers[tableField.TableName], tableField.FieldName);
            }
            if (expression is BinaryOperation binary)
            {
                object left = Evaluate(binary.LeftOperand);
                object right = Evaluate(binary.RightOperand);
                switch (binary.Token.TokenType)
                {
                    case Lexer.TType.And: return _context.Proxy.Casting.ToBoolean(left) && _context.Proxy.Casting.ToBoolean(right);
                    case Lexer.TType.Or: return _context.Proxy.Casting.ToBoolean(left) || _context.Proxy.Casting.ToBoolean(right);
                    case Lexer.TType.Equal: return _context.Proxy.Binary.AreEqual(left, right);
                    case Lexer.TType.NotEqual: return !_context.Proxy.Binary.AreEqual(left, right);
                    case Lexer.TType.Greater: return _context.Proxy.Binary.Greater(left, right);
                    case Lexer.TType.GreaterOrEqual: return _context.Proxy.Binary.Greater(left, right) || _context.Proxy.Binary.AreEqual(left, right);
                    case Lexer.TType.Smaller: return !_context.Proxy.Binary.Greater(left, right) && !_context.Proxy.Binary.AreEqual(left, right);
                    case Lexer.TType.SmallerOrEqual: return !_context.Proxy.Binary.Greater(left, right);
                    case Lexer.TType.Plus: return _context.Proxy.Binary.Add(left, right);
                    case Lexer.TType.Minus: return _context.Proxy.Binary.Substract(left, right);
                    case Lexer.TType.Star: return _context.Proxy.Binary.Multiply(left, right);
                    case Lexer.TType.Division: return _context.Proxy.Binary.Divide(left, right);
                    case Lexer.TType.IntegerDivision: return _context.Proxy.Binary.IntDivide(left, right);
                    case Lexer.TType.Mod: return _context.Proxy.Binary.Mod(left, right);
                    case Lexer.TType.LeftShift: return _context.Proxy.Binary.LeftShift(left, right);
                    case Lexer.TType.RightShift: return _context.Proxy.Binary.RightShift(left, right);
                    case Lexer.TType.BinaryAnd: return _context.Proxy.Binary.BinaryAnd(left, right);
                    case Lexer.TType.BinaryOr: return _context.Proxy.Binary.BinaryOr(left, right);
                    case Lexer.TType.BinaryXOr: return _context.Proxy.Binary.BinaryXOr(left, right);
                    case Lexer.TType.Like: return Arithmetic.LikeMatch(left, right);
                    case Lexer.TType.In:
                        if (right is System.Collections.IEnumerable values && !(right is string))
                        {
                            foreach (object item in values)
                            {
                                if (_context.Proxy.Binary.AreEqual(left, item)) return true;
                            }
                        }
                        return false;
                }
            }
            if (expression is UnaryOperation unary)
            {
                object value = Evaluate(unary.Expression);
                switch (unary.Token.TokenType)
                {
                    case Lexer.TType.Negation: return !_context.Proxy.Casting.ToBoolean(value);
                    case Lexer.TType.Plus: return value;
                    case Lexer.TType.Minus: return _context.Proxy.Binary.Substract(0, value);
                    case Lexer.TType.BinaryNot:
                        return value is long longValue ? (object)~longValue : ~Convert.ToInt32(value);
                }
            }
            if (expression is Ternary ternary)
            {
                return _context.Proxy.Casting.ToBoolean(Evaluate(ternary.Condition))
                    ? Evaluate(ternary.Left)
                    : Evaluate(ternary.Right);
            }
            if (expression is ContainerInitialisation container)
            {
                return container.Elements.Select(Evaluate).ToArray();
            }
            return new QueryGenerationHelper(_context).ComputeExpression(expression);
        }

        private bool MatchesCompanyAndValidity(Parser.Data.Query query, RuntimeRecordSnapshot row)
        {
            var helper = new QueryGenerationHelper(_context);
            if (query.CrossCompany != null && row.Values.ContainsKey("DataAreaId") && query.CrossCompany.Container != null)
            {
                object companies = helper.ComputeExpression(query.CrossCompany.Container);
                if (companies is Array array && !array.Cast<object>().Any(value =>
                    string.Equals(Convert.ToString(value), Convert.ToString(row.Value("DataAreaId")), StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            if (query.ValidTimeState != null && row.Values.ContainsKey("ValidFrom") && row.Values.ContainsKey("ValidTo"))
            {
                IComparable from = helper.ComputeExpression(query.ValidTimeState.FromDate) as IComparable;
                IComparable to = helper.ComputeExpression(query.ValidTimeState.ToDate) as IComparable;
                IComparable validFrom = row.Value("ValidFrom") as IComparable;
                IComparable validTo = row.Value("ValidTo") as IComparable;
                if (from != null && validTo != null && validTo.CompareTo(from) < 0) return false;
                if (to != null && validFrom != null && validFrom.CompareTo(to) > 0) return false;
            }
            return true;
        }

        private void ResolveBuffers(Parser.Data.Query query, ScriptObject suppliedBuffer)
        {
            if (!_buffers.ContainsKey(query.TableVariableName))
            {
                ScriptObject buffer = suppliedBuffer;
                if (buffer is null)
                {
                    try { buffer = _context.ScopeHandler.GetVar(query.TableVariableName) as ScriptObject; }
                    catch { }
                }
                if (buffer is null && _models.IsRuntimeTable(query.TableVariableName))
                {
                    buffer = _models.Create(query.TableVariableName) as ScriptObject;
                }
                if (buffer is null || !_models.IsRuntimeTableBuffer(buffer))
                {
                    throw new InvalidOperationException($"{query.TableVariableName} is not an interpreted runtime-table buffer.");
                }
                _buffers[query.TableVariableName] = buffer;
            }
            if (query.Join != null) ResolveBuffers(query.Join.Select, null);
        }

        private RuntimeRecordSnapshot EmptyRow(string bufferName)
        {
            ScriptObject buffer = _buffers[bufferName];
            _models.ClearRecord(buffer);
            return _models.CaptureRecord(buffer);
        }

        private void Apply(RuntimeQueryResult result)
        {
            foreach (var pair in result.Rows)
            {
                _models.ApplyRecord(pair.Value, _buffers[pair.Key]);
            }
        }
    }

    internal sealed class RuntimeQueryResult
    {
        public Dictionary<string, RuntimeRecordSnapshot> Rows { get; } =
            new Dictionary<string, RuntimeRecordSnapshot>(StringComparer.OrdinalIgnoreCase);

        public RuntimeQueryResult With(string name, RuntimeRecordSnapshot row)
        {
            RuntimeQueryResult clone = Clone();
            clone.Rows[name] = row.Clone();
            return clone;
        }

        public RuntimeQueryResult Clone()
        {
            var clone = new RuntimeQueryResult();
            foreach (var pair in Rows) clone.Rows[pair.Key] = pair.Value.Clone();
            return clone;
        }

        public object Value(string bufferName, string fieldName)
        {
            return Rows.TryGetValue(bufferName, out RuntimeRecordSnapshot row) ? row.Value(fieldName) : null;
        }
    }

    internal sealed class RuntimeValueComparer : IComparer<object>
    {
        public static readonly RuntimeValueComparer Instance = new RuntimeValueComparer();
        public int Compare(object left, object right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            if (left is IComparable comparable)
            {
                try { return comparable.CompareTo(Convert.ChangeType(right, left.GetType())); }
                catch { }
            }
            return string.Compare(Convert.ToString(left), Convert.ToString(right), StringComparison.OrdinalIgnoreCase);
        }
    }
}
