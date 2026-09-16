/// Does the binary writer actually put the bytes it was given on the wire?
///
/// This exists because of a corrupt package built on an Apple Silicon Mac. Its PSARC
/// header read:
///
///     50534152 f168 f168 7a6c6962 f168e01c f168e01c f168e01c f168e01c 90099720
///     "PSAR"   ver  ver  "zlib"   ToCLen   EntrySize EntryCount BlockAlloc Flags
///
/// `Header.Write` writes those nine fields in that order, and the split is exact: both
/// fields written with `WriteBytes` ("PSAR", "zlib") are correct, and every field written
/// with `WriteUInt16`/`WriteUInt32` is wrong. Four consecutive uint32 fields holding four
/// different values all came out as the same `f168e01c`, and the two uint16 fields both
/// came out as `f168`, its first two bytes.
///
/// That pattern rules out a flush, a race, an interleave or a partial write: any of those
/// would corrupt a contiguous byte range, and would not spare "zlib" sitting between two
/// corrupt fields. What it fits is the writer emitting stale stack memory instead of the
/// value, identically across consecutive calls. Every `WriteUInt*` in `BinaryWriters.fs`
/// goes through `NativePtr.stackalloc` and a `Span` built over the resulting `voidptr`;
/// `WriteBytes` writes the caller's array directly and does not.
///
/// The loop matters. Tiered compilation rejits a method in optimized code after roughly 30
/// calls, so a codegen fault in the optimized tier leaves early calls correct and breaks
/// later ones. A package writes thousands of integers before it writes its header. A test
/// that wrote one integer once would pass on a machine that cannot build a valid package.
///
/// On Linux x86_64 this passes. If it fails on arm64, the write primitives are the locus
/// and nothing built on that machine can be trusted, including the test suite's own
/// read-backs, because `BinaryReaders.fs` uses the identical construct.
module StringSmith.Pipeline.Tests.BinaryPrimitiveTests

open System
open System.IO
open Expecto
open Rocksmith2014.Common
open Rocksmith2014.Common.BinaryWriters
open Rocksmith2014.Common.BinaryReaders

/// Enough to get well past every tier-1 rejit threshold.
let private iterations = 200_000

let private describe (label: string) (i: int) (expected: byte array) (actual: byte array) =
    let hex (b: byte array) = b |> Array.map (fun x -> x.ToString("x2")) |> String.concat " "
    $"%s{label}: iteration %d{i} wrote [%s{hex actual}], expected [%s{hex expected}]"

[<Tests>]
let writerTests =
    testList "binary write primitives put the given bytes on the wire" [
        test "BigEndianBinaryWriter.WriteUInt32 over many calls" {
            use mem = new MemoryStream()
            let writer = BigEndianBinaryWriter(mem) :> IBinaryWriter
            // Values a PSARC header actually carries, plus one that exercises the high bit.
            let values = [| 30u; 65536u; 4u; 710u; 20u; 0xDEADBEEFu |]
            let mutable firstFailure = ValueNone

            for i in 0 .. iterations - 1 do
                let v = values[i % values.Length]
                mem.SetLength 0L
                writer.WriteUInt32 v
                let actual = mem.ToArray()
                let expected = [| byte (v >>> 24); byte (v >>> 16); byte (v >>> 8); byte v |]
                if firstFailure.IsNone && actual <> expected then
                    firstFailure <- ValueSome(describe "WriteUInt32" i expected actual)

            match firstFailure with
            | ValueSome msg -> failtest msg
            | ValueNone -> ()
        }

        test "BigEndianBinaryWriter.WriteUInt16 over many calls" {
            use mem = new MemoryStream()
            let writer = BigEndianBinaryWriter(mem) :> IBinaryWriter
            let values = [| 1us; 4us; 0xF168us; 0us |]
            let mutable firstFailure = ValueNone

            for i in 0 .. iterations - 1 do
                let v = values[i % values.Length]
                mem.SetLength 0L
                writer.WriteUInt16 v
                let actual = mem.ToArray()
                let expected = [| byte (v >>> 8); byte v |]
                if firstFailure.IsNone && actual <> expected then
                    firstFailure <- ValueSome(describe "WriteUInt16" i expected actual)

            match firstFailure with
            | ValueSome msg -> failtest msg
            | ValueNone -> ()
        }

        test "WriteUInt40 puts five big-endian bytes on the wire" {
            // The 40-bit fields carry every TOC entry's length and offset, so a fault here
            // corrupts the entry table rather than the header.
            use mem = new MemoryStream()
            let writer = BigEndianBinaryWriter(mem) :> IBinaryWriter
            let values = [| 0UL; 971UL; 1_436_834UL; 0xFFFFFFFFFFUL |]
            let mutable firstFailure = ValueNone

            for i in 0 .. iterations - 1 do
                let v = values[i % values.Length]
                mem.SetLength 0L
                writer.WriteUInt40 v
                let actual = mem.ToArray()
                let expected =
                    [| byte (v >>> 32); byte (v >>> 24); byte (v >>> 16); byte (v >>> 8); byte v |]
                if firstFailure.IsNone && actual <> expected then
                    firstFailure <- ValueSome(describe "WriteUInt40" i expected actual)

            match firstFailure with
            | ValueSome msg -> failtest msg
            | ValueNone -> ()
        }

        test "the mixed sequence a PSARC header writes comes out byte for byte" {
            // The exact call sequence of Header.Write, which is what produced the corrupt
            // header. Interleaving WriteBytes with the integer writes is the point: the
            // corruption spared the WriteBytes fields.
            use mem = new MemoryStream()
            let writer = BigEndianBinaryWriter(mem) :> IBinaryWriter
            let expected =
                [| 0x50uy; 0x53uy; 0x41uy; 0x52uy   // "PSAR"
                   0x00uy; 0x01uy                    // VersionMajor 1
                   0x00uy; 0x04uy                    // VersionMinor 4
                   0x7Auy; 0x6Cuy; 0x69uy; 0x62uy   // "zlib"
                   0x00uy; 0x00uy; 0x02uy; 0xC6uy   // ToCLength 710
                   0x00uy; 0x00uy; 0x00uy; 0x1Euy   // ToCEntrySize 30
                   0x00uy; 0x00uy; 0x00uy; 0x14uy   // ToCEntryCount 20
                   0x00uy; 0x01uy; 0x00uy; 0x00uy   // BlockSizeAlloc 65536
                   0x00uy; 0x00uy; 0x00uy; 0x04uy |]// ArchiveFlags 4
            let mutable firstFailure = ValueNone

            for i in 0 .. (iterations / 10) - 1 do
                mem.SetLength 0L
                writer.WriteBytes "PSAR"B
                writer.WriteUInt16 1us
                writer.WriteUInt16 4us
                writer.WriteBytes "zlib"B
                writer.WriteUInt32 710u
                writer.WriteUInt32 30u
                writer.WriteUInt32 20u
                writer.WriteUInt32 65536u
                writer.WriteUInt32 4u
                let actual = mem.ToArray()
                if firstFailure.IsNone && actual <> expected then
                    firstFailure <- ValueSome(describe "header sequence" i expected actual)

            match firstFailure with
            | ValueSome msg -> failtest msg
            | ValueNone -> ()
        }

        test "the reader gives back what the writer wrote, over many calls" {
            // BinaryReaders.fs uses the same NativePtr.stackalloc construct, so a machine
            // where the writers are broken very likely has broken readers too. Writing and
            // reading with both broken can cancel out, which is why every other assertion
            // above compares against literal bytes instead.
            let values = [| 0u; 1u; 30u; 65536u; 0xF168E01Cu; UInt32.MaxValue |]
            let mutable firstFailure = ValueNone

            for i in 0 .. iterations - 1 do
                let v = values[i % values.Length]
                use mem = new MemoryStream()
                (BigEndianBinaryWriter(mem) :> IBinaryWriter).WriteUInt32 v
                mem.Position <- 0L
                let back = (BigEndianBinaryReader(mem) :> IBinaryReader).ReadUInt32()
                if firstFailure.IsNone && back <> v then
                    firstFailure <- ValueSome $"round trip: iteration %d{i} wrote %u{v}, read back %u{back}"

            match firstFailure with
            | ValueSome msg -> failtest msg
            | ValueNone -> ()
        }
    ]
