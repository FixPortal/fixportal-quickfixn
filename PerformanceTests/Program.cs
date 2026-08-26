using System;
using System.IO;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using QuickFix;
using QuickFix.DataDictionary;
using Fields = QuickFix.Fields;

if (args is ["--check"])
{
    foreach (var payload in Enum.GetValues<EnginePipelineBenchmarks.PayloadKind>())
    {
        var benchmark = new EnginePipelineBenchmarks { Payload = payload };
        benchmark.Setup();
        Console.WriteLine($"{payload}: {benchmark.PayloadBytes} bytes");
    }
    Console.WriteLine("Benchmark payload checks passed.");
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

[MemoryDiagnoser]
[OperationsPerSecond]
public class EnginePipelineBenchmarks
{
    private const int FrameOperationsPerInvoke = 2097152;
    private const int PipelineOperationsPerInvoke = 32768;

    public enum PayloadKind
    {
        ExecutionReport,
        TradeCaptureReport,
    }

    private static readonly DateTime SendingTime = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private DataDictionary _sessionDictionary = null!;
    private DataDictionary _applicationDictionary = null!;
    private IMessageFactory _messageFactory = null!;
    private SessionID _sessionId = null!;
    private CountingCracker _cracker = null!;
    private Message _message = null!;
    private Parser _parser = null!;
    private byte[] _bytes = null!;
    private string _raw = null!;

    [Params(PayloadKind.ExecutionReport, PayloadKind.TradeCaptureReport)]
    public PayloadKind Payload { get; set; }

    public int PayloadBytes => _bytes.Length;

    [GlobalSetup]
    public void Setup()
    {
        var specPath = Path.Combine(AppContext.BaseDirectory, "spec");
        _sessionDictionary = new DataDictionary(Path.Combine(specPath, "7_FIXT11.xml"));
        _applicationDictionary = new DataDictionary(Path.Combine(specPath, "10_FIX50SP2_FP_QF.xml"));
        _messageFactory = new DefaultMessageFactory(
            [typeof(QuickFix.FIXT11.MessageFactory).Assembly, typeof(QuickFix.FIX50SP2.MessageFactory).Assembly],
            QuickFix.FixValues.ApplVerID.FIX50SP2
        );
        _sessionId = new SessionID(QuickFix.FixValues.BeginString.FIXT11, "VENUE", "CLIENT");
        _cracker = new CountingCracker();
        _raw = CreatePayload(Payload).ConstructString();
        _bytes = Encoding.ASCII.GetBytes(_raw);

        var parser = new Parser(Encoding.ASCII);
        parser.AddToStream(_bytes);
        if (!parser.ReadFixMessage(out var framed) || framed != _raw)
            throw new InvalidOperationException("Parser did not reproduce the benchmark frame.");

        _message = Build(framed);
        var expectedType = Payload == PayloadKind.ExecutionReport
            ? typeof(QuickFix.FIX50SP2.ExecutionReport)
            : typeof(QuickFix.FIX50SP2.TradeCaptureReport);
        if (_message.GetType() != expectedType)
            throw new InvalidOperationException($"Expected {expectedType.Name}, got {_message.GetType().Name}.");

        if (Payload == PayloadKind.TradeCaptureReport)
        {
            if (_message.GroupCount(Fields.Tags.NoSides) != 2)
                throw new InvalidOperationException("TradeCaptureReport did not retain both side groups.");

            for (var sideIndex = 1; sideIndex <= 2; sideIndex++)
            {
                var side = _message.GetGroup(sideIndex, Fields.Tags.NoSides);
                if (side.GroupCount(Fields.Tags.NoPartyIDs) != 3)
                    throw new InvalidOperationException("TradeCaptureReport did not retain its nested party groups.");

                for (var partyIndex = 1; partyIndex <= 3; partyIndex++)
                {
                    var party = side.GetGroup(partyIndex, Fields.Tags.NoPartyIDs);
                    if (party.GroupCount(Fields.Tags.NoPartySubIDs) != 1)
                        throw new InvalidOperationException("TradeCaptureReport did not retain its nested party sub-ID group.");
                }
            }
        }

        try
        {
            Validate(_message);
        }
        catch (TagException ex)
        {
            throw new InvalidOperationException(
                $"Benchmark payload failed dictionary validation at tag {ex.Field} with value '{ex.Value}'.",
                ex
            );
        }
        _cracker.Crack(_message, _sessionId);
        if (_cracker.Count != 1)
            throw new InvalidOperationException("MessageCracker did not invoke the expected handler.");
    }

    [IterationSetup(Target = nameof(Frame))]
    public void SetupFrame() => _parser = new Parser(Encoding.ASCII);

    [IterationSetup(Target = nameof(EndToEnd))]
    public void SetupEndToEnd() => _parser = new Parser(Encoding.ASCII);

    [Benchmark(OperationsPerInvoke = FrameOperationsPerInvoke)]
    public string Frame()
    {
        var message = "";
        for (var i = 0; i < FrameOperationsPerInvoke; i++)
        {
            _parser.AddToStream(_bytes);
            if (!_parser.ReadFixMessage(out message))
                throw new InvalidOperationException("Complete frame was not read.");
        }
        return message;
    }

    [Benchmark]
    public Message Build() => Build(_raw);

    [Benchmark]
    public Message Validate()
    {
        Validate(_message);
        return _message;
    }

    [Benchmark]
    public int Crack()
    {
        _cracker.Crack(_message, _sessionId);
        return _cracker.Count;
    }

    [Benchmark(OperationsPerInvoke = PipelineOperationsPerInvoke)]
    public int EndToEnd()
    {
        for (var i = 0; i < PipelineOperationsPerInvoke; i++)
        {
            _parser.AddToStream(_bytes);
            if (!_parser.ReadFixMessage(out var raw))
                throw new InvalidOperationException("Complete frame was not read.");

            var message = Build(raw);
            Validate(message);
            _cracker.Crack(message, _sessionId);
        }
        return _cracker.Count;
    }

    private Message Build(string raw) =>
        new MessageBuilder(
            raw,
            QuickFix.FixValues.ApplVerID.FIX50SP2,
            true,
            _sessionDictionary,
            _applicationDictionary,
            _messageFactory
        ).Build();

    private void Validate(Message message) =>
        DataDictionary.Validate(
            message,
            _sessionDictionary,
            _applicationDictionary,
            QuickFix.FixValues.BeginString.FIXT11,
            message.Header.GetString(Fields.Tags.MsgType)
        );

    private static Message CreatePayload(PayloadKind payload)
    {
        Message message = payload switch
        {
            PayloadKind.ExecutionReport => new QuickFix.FIX50SP2.ExecutionReport(
                new Fields.OrderID("ORDER-123"),
                new Fields.ExecID("EXEC-456"),
                new Fields.ExecType(Fields.ExecType.TRADE),
                new Fields.OrdStatus(Fields.OrdStatus.FILLED),
                new Fields.Side(Fields.Side.BUY),
                new Fields.LeavesQty(0),
                new Fields.CumQty(100)
            )
            {
                ClOrdID = new Fields.ClOrdID("CLIENT-789"),
                Symbol = new Fields.Symbol("MSFT"),
                LastQty = new Fields.LastQty(100),
                LastPx = new Fields.LastPx(421.25m),
                AvgPx = new Fields.AvgPx(421.25m),
            },
            PayloadKind.TradeCaptureReport => CreateTradeCaptureReport(),
            _ => throw new ArgumentOutOfRangeException(nameof(payload), payload, null),
        };

        message.Header.SetField(new Fields.BeginString(QuickFix.FixValues.BeginString.FIXT11));
        message.Header.SetField(new Fields.SenderCompID("VENUE"));
        message.Header.SetField(new Fields.TargetCompID("CLIENT"));
        message.Header.SetField(new Fields.MsgSeqNum(42));
        message.Header.SetField(new Fields.SendingTime(SendingTime));
        return message;
    }

    private static QuickFix.FIX50SP2.TradeCaptureReport CreateTradeCaptureReport()
    {
        var report = new QuickFix.FIX50SP2.TradeCaptureReport(
            new Fields.LastQty(500),
            new Fields.LastPx(421.25m)
        )
        {
            TradeReportID = new Fields.TradeReportID("TRADE-123"),
            TradeDate = new Fields.TradeDate("20260826"),
            Symbol = new Fields.Symbol("MSFT"),
        };

        for (var i = 0; i < 2; i++)
        {
            var side = new QuickFix.FIX50SP2.TradeCaptureReport.NoSidesGroup
            {
                Side = new Fields.Side(i % 2 == 0 ? Fields.Side.BUY : Fields.Side.SELL),
                OrderID = new Fields.OrderID($"ORDER-{i}"),
                ClOrdID = new Fields.ClOrdID($"CLIENT-{i}"),
                Account = new Fields.Account($"ACCOUNT-{i}"),
                SideLastQty = new Fields.SideLastQty(125),
            };

            for (var partyIndex = 0; partyIndex < 3; partyIndex++)
            {
                var party = new QuickFix.FIX50SP2.TradeCaptureReport.NoSidesGroup.NoPartyIDsGroup
                {
                    PartyID = new Fields.PartyID($"PARTY-{i}-{partyIndex}"),
                    PartyIDSource = new Fields.PartyIDSource(Fields.PartyIDSource.PROPRIETARY_CUSTOM_CODE),
                    PartyRole = new Fields.PartyRole(partyIndex + 1),
                };
                party.AddGroup(
                    new QuickFix.FIX50SP2.TradeCaptureReport.NoSidesGroup.NoPartyIDsGroup.NoPartySubIDsGroup
                    {
                        PartySubID = new Fields.PartySubID($"TRADER-{i}-{partyIndex}"),
                        PartySubIDType = new Fields.PartySubIDType(Fields.PartySubIDType.PERSON),
                    }
                );
                side.AddGroup(party);
            }

            report.AddGroup(side);
        }

        return report;
    }

    private sealed class CountingCracker : MessageCracker
    {
        public int Count { get; private set; }

        public void OnMessage(QuickFix.FIX50SP2.ExecutionReport message, SessionID sessionId) => Count++;

        public void OnMessage(QuickFix.FIX50SP2.TradeCaptureReport message, SessionID sessionId) => Count++;
    }
}
