export function installTestWebLocks(): () => void {
  const descriptor = Object.getOwnPropertyDescriptor(navigator, 'locks');
  let tail: Promise<unknown> = Promise.resolve();
  Object.defineProperty(navigator, 'locks', {
    configurable: true,
    value: {
      request<T>(_: string, operation: () => Promise<T>): Promise<T> {
        const result = tail.then(operation);
        tail = result.catch(() => undefined);
        return result;
      },
    },
  });
  return () => {
    if (descriptor) {
      Object.defineProperty(navigator, 'locks', descriptor);
    } else {
      Reflect.deleteProperty(navigator, 'locks');
    }
  };
}
