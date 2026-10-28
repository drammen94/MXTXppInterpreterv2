using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XppInterpreter.Core;

namespace XppInterpreter.Lexer
{
    /// <summary>
    /// X++ language tokenizer
    /// </summary>
    public class XppLexer : ILexer
    {
        public static readonly Dictionary<string, Word> Keywords = new Dictionary<string, Word>(StringComparer.InvariantCultureIgnoreCase);
        readonly StringReader reader;
        readonly List<IScanResult> peekResults = new List<IScanResult>();
        readonly Queue<IScanResult> _pendingTokens = new Queue<IScanResult>();
        readonly MacroPreprocessor _preprocessor;
        readonly string _sourceCode;

        char peek = ' ';
        int line = 0;

        int positionEnd = 0;
        int positionStart = 0;

        int currentPosition = -1;
        int previousPositionStart = -1;
        int previousPositionEnd = -1;

        private static DateTime MIN_DATE = new DateTime(1900, 1, 1);
        private static DateTime MAX_DATE = new DateTime(2154, 12, 31);

        public MacroPreprocessor Preprocessor => _preprocessor;

        public XppLexer(string source) : this(source, null)
        {
        }

        public XppLexer(string source, MacroPreprocessor preprocessor)
        {
            _sourceCode = source;
            reader = new StringReader(_sourceCode);
            _preprocessor = preprocessor ?? new MacroPreprocessor();
        }

        static XppLexer()
        {
            ReserveKeywords();
        }

        /// <summary>
        /// Converts an absolute source code position into [line, position] coordenates
        /// </summary>
        /// <param name="sourceCode">Source code</param>
        /// <param name="absolutPosition">Absolute position</param>
        /// <returns><see cref="SourceCodeLocation"/> with coordenate information</returns>
        public static SourceCodeLocation AnsolutePositionToSourceCodeLocation(string sourceCode, int absolutPosition)
        {
            string[] splitted = sourceCode.Substring(0, absolutPosition).Split('\n');

            int line = splitted.Count() - 1;
            int position = absolutPosition;

            if (line > 0)
            {
                int totalLength = 0;

                for (int i = 0; i <= line - 1; i++)
                {
                    totalLength += splitted[i].Length + 1;
                }

                position = absolutPosition - totalLength;
            }

            return new SourceCodeLocation(line, position);
        }

        /// <summary>
        /// Converts line, position coordinates into the absolute position
        /// </summary>
        /// <param name="sourceCode">Source code</param>
        /// <param name="location">Coordenate location</param>
        /// <returns>Absolute position</returns>
        public static int SourceCodeLocationToAbsolutePosition(string sourceCode, SourceCodeLocation location)
        {
            string[] splitted = sourceCode.Split('\n');
            int total = 0;
            for (int i = 0; i <= location.Line; i++)
            {
                // Add the carry we removed by splitting
                if (i != 0)
                {
                    total++;
                }

                if (i == location.Line)
                {
                    return total + location.Position;
                }
                else
                {
                    total += splitted[i].Length;
                }
            }

            return -1;
        }

        /// <summary>
        /// Method to be called to advance the code pointer anr returns the next token
        /// </summary>
        /// <returns></returns>
        public IScanResult GetNextToken()
        {
            return GetNextTokenInternal();
        }

        /// <summary>
        /// Gets the token at the current position if none is specified. If a position is specified,
        /// it it advances to the offset position without moving the current one
        /// </summary>
        /// <param name="offset">Token position</param>
        /// <returns>Scan result</returns>
        public IScanResult Peek(int offset = 0)
        {
            if (peekResults.Count >= offset + 1)
            {
                return peekResults[offset];
            }
            else
            {
                int skipped = peekResults.Count;
                IScanResult result = null;

                while (skipped <= offset)
                {
                    result = GetNextTokenInternal(true);
                    peekResults.Add(result);
                    skipped++;
                }

                return result;
            }
        }

        /// <summary>
        /// Reserves a token word
        /// </summary>
        /// <param name="w"></param>
        static void Reserve(Word w)
        {
            Keywords[w.Lexeme] = w;
        }

        /// <summary>
        /// Returns a new <see cref="ScanResult(Token)"/> with the <see cref="Token"/> specified
        /// and the current position
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public ScanResult ScanResult(Token token)
        {
            int resultLine = line;
            int resultPositionEnd = positionEnd - 1;
            int resultPositionStart = positionStart;

            if (positionEnd == 0)
            {
                resultLine--;
                resultPositionEnd = previousPositionEnd;
                resultPositionStart = previousPositionStart;
            }

            return new ScanResult(token, resultPositionStart, resultPositionEnd, resultLine);
        }

        /// <summary>
        /// Reads the next char and returns it only if it matches any of chars provided
        /// </summary>
        /// <param name="chars"></param>
        /// <returns></returns>
        char ReadChar(params char[] chars)
        {
            ReadChar();

            char ch = chars.FirstOrDefault(c => c == peek);

            // Not found
            if (ch != '\0')
            {
                peek = ' ';
            }

            return ch;
        }

        /// <summary>
        /// Reads the next source code char returning if it matches the one specified
        /// </summary>
        /// <param name="c">Character to check</param>
        /// <returns>True if matches, otherwise False</returns>
        bool ReadChar(char c)
        {
            ReadChar();

            if (peek != c)
            {
                return false;
            }

            peek = ' ';
            return true;
        }

        /// <summary>
        /// Reds the next character from the source code Stream
        /// </summary>
        void ReadChar()
        {
            var lastPeek = peek;

            peek = (char)reader.Read();
            
            if (lastPeek == char.MaxValue && peek == char.MaxValue)
            {
                return;
            }

            previousPositionEnd = positionEnd;
            positionEnd++;
            currentPosition++;

            if (peek == '\n')
            {
                NewLine();
            }
        }

        /// <summary>
        /// Advances the line pointer
        /// </summary>
        void NewLine()
        {
            previousPositionStart = positionStart;
            previousPositionEnd = positionEnd - 1;

            line++;
            positionStart = 0;
            positionEnd = 0;
        }

        /// <summary>
        /// Scans a date literal
        /// </summary>
        /// <param name="day">Day part of the date</param>
        /// <returns>Parsed date literal</returns>
        public DateLiteral ScanDate(int day)
        {
            ReadChar();
            long month = ScanNumber();
            if (peek != '\\')
            {
                throw new ParseException($"Expected \\ in date literal.", new Token(TType.Date), line, positionEnd);
            }

            ReadChar();
            long year = ScanNumber();

            try
            {
                var dateTime = new DateTime((int)year, (int)month, (int)day);

                if (dateTime < MIN_DATE || dateTime > MAX_DATE)
                {
                    throw new ParseException("Date out of range.", new Token(TType.Date), line, positionEnd);
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // Invalid date
                throw new ParseException($"Invalid date literal \"{day}\\{month}\\{year}\"", new Token(TType.Date), line, positionEnd);
            }

            return new DateLiteral((int)day, (int)month, (int)year);
        }

        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        /// <summary>
        /// Checks if the source code at the current position (a '-' after a number)
        /// continues with a utcdatetime literal pattern (yyyy-mm-ddThh:mm:ss)
        /// </summary>
        private bool IsUtcDateTimeAhead()
        {
            return currentPosition >= 0 &&
                System.Text.RegularExpressions.Regex.IsMatch(
                    _sourceCode.Substring(currentPosition),
                    @"^-\d{1,2}-\d{1,2}T\d{1,2}:\d{1,2}:\d{1,2}");
        }

        /// <summary>
        /// Scans a utcdatetime literal (yyyy-mm-ddThh:mm:ss). The year part has
        /// already been consumed by the number scanner.
        /// </summary>
        private DateTime ScanUtcDateTime(long year)
        {
            ReadChar();
            long month = ScanNumber();

            if (peek != '-')
            {
                throw new ParseException("Expected - in utcdatetime literal.", new Token(TType.DateTime), line, positionEnd);
            }

            ReadChar();
            long day = ScanNumber();

            if (peek != 'T')
            {
                throw new ParseException("Expected T in utcdatetime literal.", new Token(TType.DateTime), line, positionEnd);
            }

            ReadChar();
            long hour = ScanNumber();

            if (peek != ':')
            {
                throw new ParseException("Expected : in utcdatetime literal.", new Token(TType.DateTime), line, positionEnd);
            }

            ReadChar();
            long minute = ScanNumber();

            if (peek != ':')
            {
                throw new ParseException("Expected : in utcdatetime literal.", new Token(TType.DateTime), line, positionEnd);
            }

            ReadChar();
            long second = ScanNumber();

            try
            {
                return new DateTime((int)year, (int)month, (int)day, (int)hour, (int)minute, (int)second);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ParseException($"Invalid utcdatetime literal \"{year}-{month}-{day}T{hour}:{minute}:{second}\"", new Token(TType.DateTime), line, positionEnd);
            }
        }

        /// <summary>
        /// Scans the exponent part of a real literal in exponential notation (1.0e3)
        /// </summary>
        private decimal ScanExponent(decimal mantissa)
        {
            ReadChar(); // consume 'e' / 'E'

            bool negative = false;

            if (peek == '+' || peek == '-')
            {
                negative = peek == '-';
                ReadChar();
            }

            if (!char.IsDigit(peek))
            {
                throw new ParseException("Invalid exponent in real literal.", new Token(TType.Real), line, positionEnd);
            }

            long exponent = ScanNumber();

            // Beyond ~30 steps a decimal either overflows (positive exponent) or
            // underflows to zero (negative), so larger exponents need no iteration
            long iterations = Math.Min(exponent, 100);

            decimal result = mantissa;

            try
            {
                for (long i = 0; i < iterations; i++)
                {
                    result = negative ? result / 10 : result * 10;
                }
            }
            catch (OverflowException)
            {
                throw new ParseException("Real literal is out of range.", new Token(TType.Real), line, positionEnd);
            }

            if (exponent > iterations && !negative && result != 0)
            {
                throw new ParseException("Real literal is out of range.", new Token(TType.Real), line, positionEnd);
            }

            return result;
        }

        /// <summary>
        /// Scans a number
        /// </summary>
        /// <returns></returns>
        public long ScanNumber()
        {
            long value = 0;
            do
            {
                // Converts char to his number representation
                value = 10 * value + (peek - '0');
                ReadChar();

            } while (char.IsDigit(peek));

            return value;
        }

        /// <summary>
        /// Checks if the start of a string is triple quoted
        /// </summary>
        /// <param name="startingChar">Starting char of the string</param>
        /// <param name="reader">An instance of <c>StringReader</c></param>
        /// <returns>True if is triple quoted string start</returns>
        private bool IsTripleQuoteStart(char startingChar)
        {
            if (currentPosition + 1 < _sourceCode.Length && _sourceCode[currentPosition + 1] == startingChar &&
                currentPosition + 2 < _sourceCode.Length && _sourceCode[currentPosition + 2] == startingChar)
            {
                ReadChar();
                ReadChar();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if the end of a string is triple quoted
        /// </summary>
        /// <param name="currentChar">Current scanning character</param>
        /// <param name="startingChar">Starting char of the string</param>
        /// <param name="reader">An instance of <c>StringReader</c></param>
        /// <param name="builder">An instance of <c>StringBuilder</c></param>
        /// <returns></returns>
        private bool IsTripleQuoteEnd(char currentChar, char startingChar)
        {
            if (currentChar != startingChar)
                return false;

            bool secondStartingChar = currentPosition + 1 < _sourceCode.Length && _sourceCode[currentPosition + 1] == startingChar;

            if (secondStartingChar && currentPosition + 2 < _sourceCode.Length && _sourceCode[currentPosition + 2] == startingChar)
            {
                ReadChar();
                ReadChar();
                return true;
            }

            return false;
        }

        private char ReadEscapeSequence(StringReader reader)
        {
            ReadChar();

            if (peek == char.MaxValue)
            {
                throw new ParseException(
                    "Invalid escape sequence.",
                    new Token(TType.String),
                    line,
                    positionEnd
                );
            }

            switch (peek)
            {
                case 'n': return '\n';
                case 'r': return '\r';
                case 't': return '\t';
                case '\\': return '\\';
                case '"': return '"';
                case '\'': return '\'';
                case '0': return '\0';
                case 'a': return '\a';
                case 'b': return '\b';
                case 'f': return '\f';
                case 'v': return '\v';
                default: return peek;
            }
        }

        /// <summary>
        /// Scans a string token
        /// </summary>
        /// <param name="startingChar">String token delimiter</param>
        /// <returns>Read string</returns>
        public string ScanString(char startingChar, bool tripleQuotedAllowed)
        {
            StringBuilder builder = new StringBuilder();
            bool isTripleQuoted = tripleQuotedAllowed && IsTripleQuoteStart(startingChar);

            while (true)
            {
                ReadChar();
                char ch = peek;

                if (ch == char.MaxValue)
                {
                    throw new ParseException(
                        "String not finalized.",
                        new Token(TType.String),
                        line,
                        positionEnd
                    );
                }

                if (!isTripleQuoted)
                {
                    if (ch == startingChar)
                    { 
                        break;
                    }

                    if (ch == '\n' || ch == '\r')
                    {
                        throw new ParseException(
                            "Multiline strings are only allowed with triple quotes.",
                            new Token(TType.String),
                            line,
                            positionEnd
                        );
                    }
                }
                else
                {
                    if (IsTripleQuoteEnd(ch, startingChar))
                    {
                        break;
                    }
                }

                if (ch == '\\')
                {
                    builder.Append(ReadEscapeSequence(reader));
                }
                else
                {
                    builder.Append(ch);
                }
            }

            ReadChar();
            return builder.ToString();
        }

        /// <summary>
        /// Scans a verbatim string (@"...") where backslash escapes are disabled,
        /// newlines are allowed and a doubled delimiter denotes a literal delimiter
        /// </summary>
        /// <param name="startingChar">String token delimiter</param>
        /// <returns>Read string</returns>
        public string ScanVerbatimString(char startingChar)
        {
            StringBuilder builder = new StringBuilder();

            while (true)
            {
                ReadChar();
                char ch = peek;

                if (ch == char.MaxValue)
                {
                    throw new ParseException(
                        "String not finalized.",
                        new Token(TType.String),
                        line,
                        positionEnd
                    );
                }

                if (ch == startingChar)
                {
                    // A doubled delimiter is an escaped literal delimiter
                    if (currentPosition + 1 < _sourceCode.Length && _sourceCode[currentPosition + 1] == startingChar)
                    {
                        ReadChar();
                        builder.Append(startingChar);
                        continue;
                    }

                    break;
                }

                builder.Append(ch);
            }

            ReadChar();
            return builder.ToString();
        }

        /// <summary>
        /// Advances the poisition to skip a code comment
        /// </summary>
        /// <returns>True if the comment was correctly formatted</returns>
        internal bool SkipComment()
        {
            char nextPeek = (char)reader.Peek();

            if (nextPeek == '/')
            {
                while (peek != '\n' && peek != char.MaxValue)
                {
                    ReadChar();
                }

                return peek == '\n';
            }
            else if (nextPeek == '*')
            {
                while (peek != char.MaxValue && (peek != '*' || ((char)reader.Peek()) != '/'))
                {
                    ReadChar();
                }

                if (peek == '*')
                {
                    ReadChar();
                    return true;
                }
            }

            return false;
        }

        #region Preprocessor

        /// <summary>
        /// Handles a preprocessor directive or macro reference starting at '#'.
        /// Directive definitions produce no tokens; macro references enqueue their
        /// expansion tokens stamped with the usage-site position, so the debugger
        /// and error markers keep pointing at the original source.
        /// </summary>
        private void ProcessPreprocessorDirective()
        {
            int usageLine = line;
            int usageStart = positionEnd - 1;

            ReadChar(); // consume '#'

            string word = ScanDirectiveWord();

            if (word.Length == 0)
            {
                throw new ParseException("Invalid preprocessor directive.", new Token(TType.Invalid), line, positionEnd);
            }

            switch (word.ToLowerInvariant())
            {
                case "define":
                case "globaldefine":
                    {
                        ExpectDirectiveChar('.');
                        string name = ScanDirectiveWord();
                        string value = string.Empty;

                        if (peek == '(')
                        {
                            value = ScanBalancedParens();
                        }

                        _preprocessor.Define(name, value);
                        break;
                    }

                case "undef":
                    {
                        ExpectDirectiveChar('.');
                        _preprocessor.Undefine(ScanDirectiveWord());
                        break;
                    }

                case "definc":
                case "defdec":
                    {
                        ExpectDirectiveChar('.');
                        _preprocessor.IncrementValue(ScanDirectiveWord(), word.ToLowerInvariant() == "definc" ? 1 : -1);
                        break;
                    }

                case "localmacro":
                case "macro":
                    {
                        ExpectDirectiveChar('.');
                        string name = ScanDirectiveWord();
                        _preprocessor.DefineLocalMacro(name, ScanUntilEndMacro());
                        break;
                    }

                case "endmacro":
                    // Stray #endmacro outside a #localmacro definition
                    break;

                case "if":
                case "ifnot":
                    {
                        ExpectDirectiveChar('.');
                        string name = ScanDirectiveWord();
                        string value = null;

                        if (peek == '(')
                        {
                            value = ScanBalancedParens();
                        }

                        bool defined = _preprocessor.IsDefined(name);
                        bool condition = value is null
                            ? defined
                            : defined && string.Equals((_preprocessor.GetValue(name) ?? "").Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase);

                        if (word.ToLowerInvariant() == "ifnot")
                        {
                            condition = !condition;
                        }

                        if (!condition)
                        {
                            SkipConditionalBlock();
                        }

                        break;
                    }

                case "endif":
                    // Closes an active (true) conditional block
                    break;

                case "linenumber":
                    {
                        EnqueueExpansionTokens((usageLine + 1).ToString(), usageLine, usageStart);
                        break;
                    }

                case "macrolib":
                    {
                        ExpectDirectiveChar('.');
                        string libraryName = ScanDirectiveWord();
                        EnqueueExpansionTokens(ResolveMacroLibrary(libraryName), usageLine, usageStart);
                        break;
                    }

                default:
                    {
                        // Macro reference: #Name or #Name(arg1, arg2, ...)
                        if (!_preprocessor.TryGetMacro(word, out MacroPreprocessor.MacroDefinition macro))
                        {
                            // An unknown #Name is tried as a macro library include
                            if (_preprocessor.MacroLibraryResolver != null)
                            {
                                EnqueueExpansionTokens(ResolveMacroLibrary(word), usageLine, usageStart);
                                break;
                            }

                            throw new ParseException($"Macro #{word} is not defined.", new Token(TType.Invalid), usageLine, usageStart);
                        }

                        string expansion;

                        if (macro.IsLocalMacro)
                        {
                            List<string> arguments = peek == '(' ? ScanMacroArguments() : new List<string>();
                            expansion = MacroPreprocessor.SubstituteParameters(macro.Value, arguments);
                        }
                        else
                        {
                            expansion = macro.Value;
                        }

                        EnqueueExpansionTokens(expansion, usageLine, usageStart);
                        break;
                    }
            }
        }

        private string ResolveMacroLibrary(string libraryName)
        {
            if (_preprocessor.MacroLibraryResolver is null)
            {
                throw new ParseException($"Macro library {libraryName} cannot be resolved in this environment.", new Token(TType.Invalid), line, positionEnd);
            }

            string source = _preprocessor.MacroLibraryResolver(libraryName);

            if (source is null)
            {
                throw new ParseException($"Macro library {libraryName} was not found.", new Token(TType.Invalid), line, positionEnd);
            }

            return source;
        }

        /// <summary>
        /// Lexes a macro expansion with a sub-lexer sharing this preprocessor state and
        /// enqueues the resulting tokens stamped with the usage-site location.
        /// </summary>
        private void EnqueueExpansionTokens(string expansion, int usageLine, int usageStart)
        {
            if (string.IsNullOrWhiteSpace(expansion))
            {
                return;
            }

            if (_preprocessor.ExpansionDepth >= MacroPreprocessor.MaxExpansionDepth)
            {
                throw new ParseException(
                    $"Macro expansion exceeds the maximum depth of {MacroPreprocessor.MaxExpansionDepth} (recursive macro definition?).",
                    new Token(TType.Invalid),
                    usageLine,
                    usageStart);
            }

            int usageEnd = Math.Max(usageStart, positionEnd - 1);

            _preprocessor.ExpansionDepth++;

            try
            {
                var subLexer = new XppLexer(expansion, _preprocessor);

                while (true)
                {
                    IScanResult result = subLexer.GetNextToken();

                    if (result.Token.TokenType == TType.EOF)
                    {
                        break;
                    }

                    _pendingTokens.Enqueue(new ScanResult(result.Token, usageStart, usageEnd, usageLine));
                }
            }
            finally
            {
                _preprocessor.ExpansionDepth--;
            }
        }

        private string ScanDirectiveWord()
        {
            StringBuilder builder = new StringBuilder();

            while (char.IsLetterOrDigit(peek) || peek == '_')
            {
                builder.Append(peek);
                ReadChar();
            }

            return builder.ToString();
        }

        private void ExpectDirectiveChar(char expected)
        {
            if (peek != expected)
            {
                throw new ParseException($"Expected '{expected}' in preprocessor directive.", new Token(TType.Invalid), line, positionEnd);
            }

            ReadChar();
        }

        /// <summary>
        /// Appends a complete string literal (honoring backslash escapes) to the
        /// builder, leaving peek after the closing quote
        /// </summary>
        private void AppendStringLiteral(StringBuilder builder)
        {
            char quote = peek;
            builder.Append(peek);
            ReadChar();

            while (peek != quote && peek != char.MaxValue)
            {
                if (peek == '\\')
                {
                    builder.Append(peek);
                    ReadChar();

                    if (peek == char.MaxValue)
                    {
                        return;
                    }
                }

                builder.Append(peek);
                ReadChar();
            }

            if (peek != char.MaxValue)
            {
                builder.Append(peek);
                ReadChar();
            }
        }

        /// <summary>
        /// Captures the raw text between balanced parentheses, skipping over string
        /// literals so parentheses inside strings do not affect the balance
        /// </summary>
        private string ScanBalancedParens()
        {
            StringBuilder builder = new StringBuilder();
            int depth = 1;

            ReadChar(); // consume '('

            while (true)
            {
                if (peek == char.MaxValue)
                {
                    throw new ParseException("Preprocessor directive not finalized.", new Token(TType.Invalid), line, positionEnd);
                }

                if (peek == '\'' || peek == '"')
                {
                    AppendStringLiteral(builder);
                    continue;
                }

                if (peek == '(')
                {
                    depth++;
                }
                else if (peek == ')')
                {
                    depth--;

                    if (depth == 0)
                    {
                        break;
                    }
                }

                builder.Append(peek);
                ReadChar();
            }

            ReadChar(); // move past ')'
            return builder.ToString();
        }

        /// <summary>
        /// Captures raw macro arguments between parentheses, split on top-level commas
        /// </summary>
        private List<string> ScanMacroArguments()
        {
            var arguments = new List<string>();
            StringBuilder builder = new StringBuilder();
            int depth = 1;

            ReadChar(); // consume '('

            while (true)
            {
                if (peek == char.MaxValue)
                {
                    throw new ParseException("Macro arguments not finalized.", new Token(TType.Invalid), line, positionEnd);
                }

                if (peek == '\'' || peek == '"')
                {
                    // Commas and parentheses inside string arguments are literal text
                    AppendStringLiteral(builder);
                    continue;
                }

                if (peek == '(')
                {
                    depth++;
                    builder.Append(peek);
                }
                else if (peek == ')')
                {
                    depth--;

                    if (depth == 0)
                    {
                        break;
                    }

                    builder.Append(peek);
                }
                else if (peek == ',' && depth == 1)
                {
                    arguments.Add(builder.ToString().Trim());
                    builder.Clear();
                }
                else
                {
                    builder.Append(peek);
                }

                ReadChar();
            }

            ReadChar(); // move past ')'

            if (builder.Length > 0 || arguments.Count > 0)
            {
                arguments.Add(builder.ToString().Trim());
            }

            return arguments;
        }

        /// <summary>
        /// Reads the directive word following a '#' at the current position without
        /// consuming any characters
        /// </summary>
        private string PeekDirectiveWord()
        {
            int wordStart = currentPosition + 1;
            int wordEnd = wordStart;

            while (wordEnd < _sourceCode.Length &&
                (char.IsLetterOrDigit(_sourceCode[wordEnd]) || _sourceCode[wordEnd] == '_'))
            {
                wordEnd++;
            }

            return _sourceCode.Substring(wordStart, wordEnd - wordStart);
        }

        /// <summary>
        /// Captures the raw body of a #localmacro definition up to its matching
        /// #endmacro, skipping string literals, matching the directive name on a word
        /// boundary, and honoring nested #localmacro definitions
        /// </summary>
        private string ScanUntilEndMacro()
        {
            StringBuilder builder = new StringBuilder();
            int depth = 1;

            while (true)
            {
                if (peek == char.MaxValue)
                {
                    throw new ParseException("#endmacro expected.", new Token(TType.Invalid), line, positionEnd);
                }

                if (peek == '\'' || peek == '"')
                {
                    AppendStringLiteral(builder);
                    continue;
                }

                if (peek == '#')
                {
                    string word = PeekDirectiveWord();

                    if (string.Equals(word, "endmacro", StringComparison.OrdinalIgnoreCase))
                    {
                        depth--;

                        if (depth == 0)
                        {
                            // Consume '#endmacro'
                            for (int i = 0; i <= word.Length; i++)
                            {
                                ReadChar();
                            }

                            break;
                        }
                    }
                    else if (string.Equals(word, "localmacro", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(word, "macro", StringComparison.OrdinalIgnoreCase))
                    {
                        depth++;
                    }
                }

                builder.Append(peek);
                ReadChar();
            }

            return builder.ToString();
        }

        /// <summary>
        /// Skips a false conditional region up to its matching #endif, honoring
        /// nesting and ignoring directives inside string literals and comments
        /// </summary>
        private void SkipConditionalBlock()
        {
            int depth = 1;

            while (depth > 0)
            {
                if (peek == char.MaxValue)
                {
                    throw new ParseException("#endif expected.", new Token(TType.Invalid), line, positionEnd);
                }

                if (peek == '\'' || peek == '"')
                {
                    char quote = peek;
                    ReadChar();

                    while (peek != quote && peek != char.MaxValue)
                    {
                        if (peek == '\\')
                        {
                            ReadChar();

                            if (peek == char.MaxValue)
                            {
                                break;
                            }
                        }

                        ReadChar();
                    }

                    if (peek != char.MaxValue)
                    {
                        ReadChar();
                    }

                    continue;
                }

                if (peek == '/')
                {
                    SkipComment();
                    ReadChar();
                    continue;
                }

                if (peek == '#')
                {
                    ReadChar();
                    string word = ScanDirectiveWord().ToLowerInvariant();

                    if (word == "if" || word == "ifnot")
                    {
                        depth++;
                    }
                    else if (word == "endif")
                    {
                        depth--;
                    }
                }
                else
                {
                    ReadChar();
                }
            }
        }

        #endregion

        /// <summary>
        /// Actual method to read the next token
        /// </summary>
        /// <param name="isPeek">If needs to be retrieved from the previous ones</param>
        /// <returns>Next token result</returns>
        internal IScanResult GetNextTokenInternal(bool isPeek = false)
        {
            if (!isPeek && peekResults.Count > 0)
            {
                IScanResult result = peekResults.First();
                peekResults.RemoveAt(0);
                return result;
            }

            // Return macro expansion tokens before scanning further
            if (_pendingTokens.Count > 0)
            {
                return _pendingTokens.Dequeue();
            }

            // Keep reading until we find an elegible token
            for (; ; )
            {
                if (peek == ' ' || peek == '\t' || peek == '\r' || peek == '\n')
                {
                    ReadChar();
                }
                else if (peek == '/' && SkipComment())
                {
                    ReadChar();
                }
                else if (peek == '#')
                {
                    ProcessPreprocessorDirective();

                    if (_pendingTokens.Count > 0)
                    {
                        return _pendingTokens.Dequeue();
                    }
                }
                else
                {
                    break;
                }
            }

            previousPositionStart = positionStart;
            positionStart = positionEnd - 1;

            // We reached end of file
            if (peek == char.MaxValue)
            {
                return ScanResult(new Token(TType.EOF));
            }

            switch (peek)
            {
                case '=':
                    {
                        if (ReadChar('='))
                        {
                            ReadChar();
                            return ScanResult(Word.Equal);
                        }
                        else
                        {
                            return ScanResult(new Token(TType.Assign));
                        }
                    }
                case '!':
                    {
                        if (ReadChar('='))
                        {
                            ReadChar();
                            return ScanResult(Word.NotEqual);
                        }
                        else
                        {
                            return ScanResult(new Token(TType.Negation));
                        }
                    }
                case '"':
                case '\'':
                    {
                        return ScanResult(new String(ScanString(peek, peek == '"')));
                    }
                case '@':
                    {
                        ReadChar();

                        // Verbatim string: @"..." (no escape sequences, multiline allowed)
                        if (peek == '"' || peek == '\'')
                        {
                            return ScanResult(new String(ScanVerbatimString(peek)));
                        }

                        // Identifier escape: @keyword is treated as a plain identifier
                        if (char.IsLetter(peek) || peek == '_')
                        {
                            StringBuilder idBuilder = new StringBuilder();

                            do
                            {
                                idBuilder.Append(peek);
                                ReadChar();
                            } while (char.IsLetterOrDigit(peek) || peek == '_');

                            return ScanResult(new Word(idBuilder.ToString(), TType.Id));
                        }

                        throw new ParseException("Character not recognized.", new Token(TType.Invalid), line, previousPositionEnd);
                    }
                case '<':
                    {
                        char found = ReadChar('=', '<');

                        if (found != '\0')
                        {
                            ReadChar();

                            if (found == '=')
                            {
                                return ScanResult(Word.LessOrEqual);
                            }
                            else if (found == '<')
                            {
                                return ScanResult(Word.LeftShift);
                            }
                        }

                        return ScanResult(new Token(TType.Smaller));
                    }
                case '>':
                    {
                        char found = ReadChar('=', '>');

                        if (found != '\0')
                        {
                            ReadChar();

                            if (found == '=')
                            {
                                return ScanResult(Word.GreaterOrEqual);
                            }
                            else if (found == '>')
                            {
                                return ScanResult(Word.RightShift);
                            }
                        }

                        return ScanResult(new Token(TType.Greater));
                    }
                case '+':
                    {
                        char found = ReadChar('+', '=');

                        if (found != '\0')
                        {
                            ReadChar();

                            if (found == '+')
                            {
                                return ScanResult(Word.Increment);
                            }
                            else if (found == '=')
                            {
                                return ScanResult(Word.PlusAssignment);
                            }
                        }

                        return ScanResult(new Token(TType.Plus));
                    }
                case '-':
                    {
                        char found = ReadChar('-', '=');

                        if (found != '\0')
                        {
                            ReadChar();

                            if (found == '-')
                            {
                                return ScanResult(new Token(TType.Decrement));
                            }
                            else if (found == '=')
                            {
                                return ScanResult(new Token(TType.MinusAssignment));
                            }
                        }
                        return ScanResult(new Token(TType.Minus));
                    }

                case ':':
                    {
                        if (ReadChar(':'))
                        {
                            ReadChar();

                            return ScanResult(new Token(TType.StaticDoubleDot));
                        }
                        else
                        {
                            return ScanResult(new Token(TType.DoubleDot));
                        }
                    }
                case '&':
                    {
                        if (ReadChar('&'))
                        {
                            ReadChar(); 
                            return ScanResult(new Token(TType.And));
                        }
                        else
                        {
                            return ScanResult(new Token(TType.BinaryAnd));
                        }
                    }
                case '|':
                    {
                        if (ReadChar('|'))
                        {
                            ReadChar(); 
                            return ScanResult(new Token(TType.Or));
                        }
                        else
                        {
                            return ScanResult(new Token(TType.BinaryOr));
                        }
                    }
                default:
                    {
                        TType tokenType = Token.TagHelper.GetTokenType(peek);
                        if (tokenType != TType.Invalid)
                        {
                            ReadChar();
                            IScanResult result = ScanResult(new Token(tokenType));
                            return result;
                        }
                    }
                    break;
            }

            if (char.IsDigit(peek))
            {
                // Hexadecimal literals (0xFF)
                if (peek == '0' && currentPosition + 1 < _sourceCode.Length &&
                    (_sourceCode[currentPosition + 1] == 'x' || _sourceCode[currentPosition + 1] == 'X') &&
                    currentPosition + 2 < _sourceCode.Length && IsHexDigit(_sourceCode[currentPosition + 2]))
                {
                    ReadChar(); // consume '0'
                    long hexValue = 0;

                    do
                    {
                        ReadChar(); // first iteration consumes 'x'
                        if (IsHexDigit(peek))
                        {
                            hexValue = hexValue * 16 + Convert.ToInt32(peek.ToString(), 16);
                        }
                    } while (IsHexDigit(peek));

                    if (hexValue > int.MaxValue)
                    {
                        return ScanResult(new Int64(hexValue));
                    }

                    return ScanResult(new Int32((int)hexValue));
                }

                long value = ScanNumber();

                if (peek != '.')
                {
                    if (peek == '\\')
                    {
                        return ScanResult(new Date(ScanDate((int)value)));
                    }

                    if (peek == '-' && IsUtcDateTimeAhead())
                    {
                        return ScanResult(new BaseType(ScanUtcDateTime(value), TType.DateTime));
                    }

                    if (peek == 'e' || peek == 'E')
                    {
                        return ScanResult(new Real(ScanExponent(value)));
                    }

                    if (value > int.MaxValue)
                    {
                        return ScanResult(new Int64(value));
                    }
                    else
                    {
                        return ScanResult(new Int32((int)value));
                    }
                }

                decimal x = value, d = 10;
                while (true)
                {
                    ReadChar();
                    if (!char.IsDigit(peek))
                    {
                        break;
                    }

                    x += (peek - '0') / d;
                    d *= 10;
                }

                if (peek == 'e' || peek == 'E')
                {
                    return ScanResult(new Real(ScanExponent(x)));
                }

                return ScanResult(new Real(x));
            }

            if (char.IsLetter(peek) || peek == '_')
            {
                StringBuilder builder = new StringBuilder();

                do
                {
                    builder.Append(peek);
                    ReadChar();
                } while (char.IsLetterOrDigit(peek) || peek == '_');

                string s = builder.ToString();

                if (Keywords.ContainsKey(s))
                {
                    return ScanResult(Keywords[s]);
                }
                else
                {
                    return ScanResult(new Word(s, TType.Id));
                }
            }

            throw new ParseException("Character not recognized.", new Token(TType.Invalid), line, previousPositionEnd);
        }

        /// <summary>
        /// Reserves all the word tokens
        /// </summary>
        internal static void ReserveKeywords()
        {
            Reserve(Word.If);
            Reserve(Word.Else);
            Reserve(Word.True);
            Reserve(Word.False);
            Reserve(Word.While);
            Reserve(Word.Do);
            Reserve(Word.New);
            Reserve(Word.For);
            Reserve(Word.Continue);
            Reserve(Word.Break);
            Reserve(Word.Breakpoint);
            Reserve(Word.Null);
            Reserve(Word.Try);
            Reserve(Word.Finally);
            Reserve(Word.Catch);
            Reserve(Word.Throw);
            Reserve(Word.Retry);
            Reserve(Word.Using);
            Reserve(Word.IntegerDivision);
            Reserve(Word.Mod);
            Reserve(Word.Increment);
            Reserve(Word.Decrement);
            Reserve(Word.PlusAssignment);
            Reserve(Word.MinusAssignment);
            Reserve(Word.Or);
            Reserve(Word.And);
            Reserve(Word.Switch);
            Reserve(Word.Case);
            Reserve(Word.Print);
            Reserve(Word.Default);
            Reserve(Word.TtsCommit);
            Reserve(Word.TtsAbort);
            Reserve(Word.TtsBegin);
            Reserve(Word.ChangeCompany);
            Reserve(Word.Unchecked);
            Reserve(Word.Flush);
            Reserve(Word.Select);
            Reserve(Word.From);
            Reserve(Word.Where);
            Reserve(Word.Join);
            Reserve(Word.Exists);
            Reserve(Word.NotExists);
            Reserve(Word.Outer);
            Reserve(Word.By);
            Reserve(Word.Group);
            Reserve(Word.Order);
            Reserve(Word.FirstOnly);
            Reserve(Word.FirstFast);
            Reserve(Word.FirstOnly10);
            Reserve(Word.FirstOnly100);
            Reserve(Word.FirstOnly1000);
            Reserve(Word.Reverse);
            Reserve(Word.RepeatableRead);
            Reserve(Word.CrossCompany);
            Reserve(Word.ValidTimeState);
            Reserve(Word.ForceLiterals);
            Reserve(Word.ForceNestedLoops);
            Reserve(Word.ForcePlaceHolders);
            Reserve(Word.OptimisticLock);
            Reserve(Word.PessimisticLock);
            Reserve(Word.GenerateOnly);
            Reserve(Word.ForceSelectOrder);
            Reserve(Word.ForUpdate);
            Reserve(Word.UpdateRecordset);
            Reserve(Word.InsertRecordset);
            Reserve(Word.DeleteFrom);
            Reserve(Word.Setting);
            Reserve(Word.MaxOf);
            Reserve(Word.MinOf);
            Reserve(Word.Count);
            Reserve(Word.Avg);
            Reserve(Word.Sum);
            Reserve(Word.Asc);
            Reserve(Word.Desc);
            Reserve(Word.Index);
            Reserve(Word.Next);
            Reserve(Word.In);
            Reserve(Word.Like);
            Reserve(Word.Is);
            Reserve(Word.As);
            Reserve(Word.NoFetch);

            Reserve(Type.Int32);
            Reserve(Type.Int64);
            Reserve(Type.Anytype);
            Reserve(Type.Container);
            Reserve(Type.Real);
            Reserve(Type.DateTime);
            Reserve(Type.Date);
            Reserve(Type.TimeOfDay);
            Reserve(Type.Str);
            Reserve(Type.Var);
            Reserve(Type.Boolean);
            Reserve(Type.Guid);

            Reserve(Word.Void);
            Reserve(Word.Return);
            Reserve(Word.EventHandler);

            Reserve(Word.Class);
            Reserve(Word.Interface);
            Reserve(Word.Extends);
            Reserve(Word.Implements);
            Reserve(Word.Public);
            Reserve(Word.Protected);
            Reserve(Word.Private);
            Reserve(Word.Internal);
            Reserve(Word.Static);
            Reserve(Word.Final);
            Reserve(Word.Abstract);
            Reserve(Word.Const);
            Reserve(Word.ReadOnly);
            Reserve(Word.Delegate);
            Reserve(Word.ByRef);
            Reserve(Word.Client);
            Reserve(Word.Server);
            Reserve(Word.Display);
            Reserve(Word.Edit);
        }
    }
}
