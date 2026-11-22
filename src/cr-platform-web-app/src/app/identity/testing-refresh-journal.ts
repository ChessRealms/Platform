export function createTestRefreshJournal() {
  let pending = false;
  return {
    isPending: async () => pending,
    setPending: async (value: boolean) => { pending = value; },
  };
}
