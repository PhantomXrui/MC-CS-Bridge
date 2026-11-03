using MinecraftBridge;

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
static KeyValuePair<uint, int>[] Snapshot(UniqueTAllocation allocator) =>
    allocator.Assignments.OrderBy(pair => pair.Key).ToArray();

var allocator = new UniqueTAllocation(34);
uint[] original = Enumerable.Range(1, 34).Select(i => (uint)(1000 + i)).ToArray();
Check(allocator.Reconcile(original.Reverse().Concat(original.Take(7))), "34 distinct handles fit despite duplicates");
Check(allocator.Assignments.Count == 34, "Exactly 34 assignments");
Check(allocator.Assignments.Values.Distinct().Count() == 34, "All 34 skins are unique");
for (int i = 0; i < original.Length; i++)
    Check(allocator.Assignments[original[i]] == i, "Initial assignment follows sorted full handles");

var full = Snapshot(allocator);
Check(allocator.Reconcile(original.Reverse().Concat(original)), "Repeated reconciliation succeeds");
Check(full.SequenceEqual(Snapshot(allocator)), "Reordering and duplicates are idempotent");

// An over-capacity request must not release old identities even if its roster differs.
Check(!allocator.Reconcile(Enumerable.Range(2000, 35).Select(i => (uint)i)), "Overflow is rejected");
Check(full.SequenceEqual(Snapshot(allocator)), "Overflow preserves the entire old mapping");

uint[] survivors = original.Where((_, index) => index != 2 && index != 11).ToArray();
Check(allocator.Reconcile(survivors), "Departed controllers can be removed");
Check(!allocator.Assignments.ContainsKey(original[2]) && !allocator.Assignments.ContainsKey(original[11]), "Departed identities are released");
foreach (uint handle in survivors)
    Check(allocator.Assignments[handle] == full.Single(pair => pair.Key == handle).Value, "Survivor keeps its skin, including a dead but live controller");
Check(allocator.Reconcile(survivors.Concat(new uint[] { 5000, 10, 10 })), "Newcomers fit after departures");
Check(allocator.Assignments[10] == 2 && allocator.Assignments[5000] == 11, "Sorted newcomers receive lowest released indices");
Check(allocator.Assignments.Values.Distinct().Count() == 34, "Reused indices remain unique");

// Low handle bits model a slot; high bits model its generation. They are opaque identities.
static uint Handle(uint generation, uint slot) => (generation << 15) | slot;
var slots = new UniqueTAllocation(64);
uint[] firstGeneration = Enumerable.Range(0, 64).Select(slot => Handle(1, (uint)slot)).ToArray();
Check(slots.Reconcile(firstGeneration.Reverse()), "All 64 slots fit");
uint replacedOld = Handle(1, 5), replacedNew = Handle(2, 5);
uint[] nextRoster = firstGeneration.Where(handle => handle != replacedOld).Append(replacedNew).ToArray();
Check(slots.Reconcile(nextRoster), "Same slot with a new raw handle is accepted as a newcomer");
Check(!slots.Assignments.ContainsKey(replacedOld) && slots.Assignments.ContainsKey(replacedNew), "Old generation identity is not retained");
Check(slots.Assignments[replacedNew] == 5, "New generation may reuse the released skin index");
foreach (uint handle in firstGeneration.Where(handle => handle != replacedOld))
    Check(slots.Assignments[handle] == (int)(handle & 0x7fff), "Other 63 identities retain their skins");
Check(slots.Assignments.Values.Distinct().Count() == 64, "Slot reuse does not duplicate skins");
Check(!slots.Reconcile(nextRoster.Append(replacedOld)), "Old and new generations together count as separate identities");
Check(!slots.Assignments.ContainsKey(replacedOld), "Failed generation overlap does not restore a stale identity");

var readOnlyView = allocator.Assignments;
allocator.Clear();
Check(readOnlyView.Count == 0 && allocator.Assignments.Count == 0, "Clear empties the exposed read-only view");
Check(allocator.Reconcile(new uint[] { 20, 10 }), "Allocator can be reused after Clear");
Check(allocator.Assignments[10] == 0 && allocator.Assignments[20] == 1, "Clear releases every index");
var empty = new UniqueTAllocation(0);
Check(empty.Reconcile(Array.Empty<uint>()), "Zero capacity accepts an empty roster");
Check(!empty.Reconcile(new uint[] { 1 }) && empty.Assignments.Count == 0, "Zero capacity rejects a nonempty roster atomically");
bool rejectedNegative = false;
try { _ = new UniqueTAllocation(-1); } catch (ArgumentOutOfRangeException) { rejectedNegative = true; }
Check(rejectedNegative, "Negative capacity is rejected");
bool rejectedNull = false;
var beforeNull = Snapshot(allocator);
try { allocator.Reconcile(null!); } catch (ArgumentNullException) { rejectedNull = true; }
Check(rejectedNull && beforeNull.SequenceEqual(Snapshot(allocator)), "Null input fails without mutation");

Console.WriteLine($"PASS: {assertions} assertions; 34 unique skins, survivor retention, release/reuse, atomic overflow, 64-slot generations, clear and bounds.");
