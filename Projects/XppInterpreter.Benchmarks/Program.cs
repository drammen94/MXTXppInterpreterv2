using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using XppInterpreter.Interpreter;
using XppInterpreter.Interpreter.Bytecode;
using XppInterpreter.Lexer;
using XppInterpreter.Parser;
using XppInterpreter.Tests;

namespace XppInterpreter.Benchmarks
{
    internal static class Program
    {
        private const string SmallSource = @"
            int value = 1;
            value += 41;
            capture(value);
        ";

        private const string LoopSource = @"
            int total = 0;
            for (int i = 0; i < 1000; i++)
            {
                total += i;
            }
            capture(total);
        ";

        private const string RuntimeTableSource = @"
            [RuntimeTable]
            class RuntimeBenchmarkRow
            {
                [RuntimeKey] public int Key;
                public int Value;
            }

            RuntimeBenchmarkRow row;
            int total = 0;
            for (int i = 1; i <= 100; i++)
            {
                row.Key = i;
                row.Value = i * 2;
                row.insert();
                row.clear();
            }

            while select row where row.Value > 100
            {
                total += row.Value;
            }
            capture(total);
        ";

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && string.Equals(args[0], "--single", StringComparison.OrdinalIgnoreCase))
                {
                    RunSingle();
                    return 0;
                }

                RunBenchmarks();
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void RunSingle()
        {
            var proxy = TestProxyFactory.Create();
            var interpreter = new XppInterpreter.Interpreter.XppInterpreter(proxy);
            EditToResult(SmallSource, proxy, interpreter);
            RequireCaptured(42);
            Console.WriteLine("OK");
        }

        private static void RunBenchmarks()
        {
            var proxy = TestProxyFactory.Create();
            var interpreter = new XppInterpreter.Interpreter.XppInterpreter(proxy);

            ParseResult parsed = Parse(SmallSource, proxy);
            ByteCode smallByteCode = interpreter.Compile((XppInterpreter.Parser.Program)parsed.AST);
            ByteCode loopByteCode = Compile(LoopSource, proxy, interpreter);

            // Force CLR JIT and static initializers before collecting warm-path samples.
            for (int i = 0; i < 200; i++)
            {
                EditToResult(SmallSource, proxy, interpreter);
                interpreter.Interpret(smallByteCode, new RuntimeContext(proxy, smallByteCode));
            }

            Console.WriteLine("X++ interpreter latency benchmark (Release, warm in-process)");
            Console.WriteLine("Timer frequency: {0:N0} ticks/second", Stopwatch.Frequency);
            Console.WriteLine();
            Console.WriteLine("{0,-31} {1,8} {2,10} {3,10} {4,10} {5,10}", "Scenario", "Runs", "Mean ms", "P50 ms", "P95 ms", "P99 ms");

            Print(Measure("Parse + semantic validation", 5000, delegate
            {
                Parse(SmallSource, proxy);
            }));

            Print(Measure("Bytecode generation", 5000, delegate
            {
                interpreter.Compile((XppInterpreter.Parser.Program)parsed.AST);
            }));

            Print(Measure("Hot bytecode execution", 10000, delegate
            {
                interpreter.Interpret(smallByteCode, new RuntimeContext(proxy, smallByteCode));
            }));

            Print(Measure("Full edit-to-result", 5000, delegate
            {
                EditToResult(SmallSource, proxy, interpreter);
            }));

            Print(Measure("Execute 1,000-iteration loop", 1000, delegate
            {
                interpreter.Interpret(loopByteCode, new RuntimeContext(proxy, loopByteCode));
            }));

            Print(Measure("Runtime table 100 rows/query", 300, delegate
            {
                EditToResult(RuntimeTableSource, proxy, interpreter);
            }));

            RequireCaptured(7550);
        }

        private static ParseResult Parse(string source, XppInterpreter.Interpreter.Proxy.XppProxy proxy)
        {
            ParseResult result = new XppParser(new XppLexer(source), proxy).Parse();
            if (result.HasErrors)
            {
                throw new InvalidOperationException(string.Join(" | ", result.Errors.Select(error => error.Message)));
            }

            return result;
        }

        private static ByteCode Compile(
            string source,
            XppInterpreter.Interpreter.Proxy.XppProxy proxy,
            XppInterpreter.Interpreter.XppInterpreter interpreter)
        {
            ParseResult parsed = Parse(source, proxy);
            return interpreter.Compile((XppInterpreter.Parser.Program)parsed.AST);
        }

        private static void EditToResult(
            string source,
            XppInterpreter.Interpreter.Proxy.XppProxy proxy,
            XppInterpreter.Interpreter.XppInterpreter interpreter)
        {
            ByteCode byteCode = Compile(source, proxy, interpreter);
            interpreter.Interpret(byteCode, new RuntimeContext(proxy, byteCode));
        }

        private static Measurement Measure(string name, int iterations, Action action)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var samples = new double[iterations];
            for (int i = 0; i < iterations; i++)
            {
                long started = Stopwatch.GetTimestamp();
                action();
                long stopped = Stopwatch.GetTimestamp();
                samples[i] = (stopped - started) * 1000.0 / Stopwatch.Frequency;
            }

            Array.Sort(samples);
            return new Measurement(
                name,
                iterations,
                samples.Average(),
                Percentile(samples, 0.50),
                Percentile(samples, 0.95),
                Percentile(samples, 0.99));
        }

        private static double Percentile(IReadOnlyList<double> sorted, double percentile)
        {
            int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Count - 1, index))];
        }

        private static void Print(Measurement measurement)
        {
            Console.WriteLine(
                "{0,-31} {1,8:N0} {2,10:F4} {3,10:F4} {4,10:F4} {5,10:F4}",
                measurement.Name,
                measurement.Iterations,
                measurement.MeanMilliseconds,
                measurement.P50Milliseconds,
                measurement.P95Milliseconds,
                measurement.P99Milliseconds);
        }

        private static void RequireCaptured(int expected)
        {
            int actual = Convert.ToInt32(TestProxyFactory.LastCaptured);
            if (actual != expected)
            {
                throw new InvalidOperationException("Expected captured value " + expected + ", got " + actual + ".");
            }
        }

        private sealed class Measurement
        {
            public Measurement(string name, int iterations, double meanMilliseconds, double p50Milliseconds, double p95Milliseconds, double p99Milliseconds)
            {
                Name = name;
                Iterations = iterations;
                MeanMilliseconds = meanMilliseconds;
                P50Milliseconds = p50Milliseconds;
                P95Milliseconds = p95Milliseconds;
                P99Milliseconds = p99Milliseconds;
            }

            public string Name { get; private set; }
            public int Iterations { get; private set; }
            public double MeanMilliseconds { get; private set; }
            public double P50Milliseconds { get; private set; }
            public double P95Milliseconds { get; private set; }
            public double P99Milliseconds { get; private set; }
        }
    }
}
