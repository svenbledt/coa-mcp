# Client DBC patch notes

How the client applies server-sent DBC patch records (the CoA64 port's module for it is called ClientDbcPatch).

## Wire record program (`ReadValues`)

The handler reads a record item by item:

| Item | Reads |
|---|---|
| `b` / `h` / `d` | one value each |
| `q` | two dwords |
| `x` | four dwords |
| `_` | a skipped byte |
| `L` | sixteen u32-length locale strings, of which slot 0 is kept |
| `p` | a byte packed into the current value (`|` closes it) |
| `B` | a u32 of which only the low byte is kept (four pack into one value) |
| `s` | a C string |
| `z` | a u32-length string |

Strings are copied into their own allocation and the value is the pointer. An overwritten row's old strings leak, as
in the original.

## Index insert (`Insert`)

A new row's id inside `[min, max]` takes its slot. Outside it, the index is reallocated to cover the id and the old
rows are copied.

- **Old maximum.** The original's copy loop stops short of the old maximum, losing that row on every growing insert.
  The CoA64 port keeps it.
- **Empty tables.** A table with no rows can have no index and `min == max == 0`, so the copy is skipped without an
  index.
- **RLE tables.** A `WowClientDB_RLE` index lives inside the table's record block (`ownsIndex` false), so it is not
  released. The original's free of that interior pointer is silent on its 32-bit heap and would end the x64 process.
