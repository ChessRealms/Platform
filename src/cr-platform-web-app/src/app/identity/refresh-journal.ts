import { DOCUMENT } from '@angular/common';
import { inject, Injectable } from '@angular/core';

const storeName = 'refresh';
const pendingKey = 'pending';

@Injectable({ providedIn: 'root' })
export class RefreshJournal {
  private readonly window = inject(DOCUMENT).defaultView!;
  private database?: Promise<IDBDatabase>;

  async isPending(): Promise<boolean> {
    return (await this.transact('readonly', store => store.get(pendingKey))) === true;
  }

  async setPending(pending: boolean): Promise<void> {
    if (pending) {
      await this.transact('readwrite', store => store.put(true, pendingKey));
    } else {
      await this.transact('readwrite', store => store.delete(pendingKey));
    }
  }

  private async transact<T>(
    mode: 'readonly' | 'readwrite',
    createRequest: (store: IDBObjectStore) => IDBRequest<T>,
  ): Promise<T> {
    const database = await this.open();
    return new Promise((resolve, reject) => {
      const transaction = database.transaction(storeName, mode, { durability: 'strict' });
      const request = createRequest(transaction.objectStore(storeName));
      transaction.oncomplete = () => resolve(request.result);
      transaction.onabort = () => reject(transaction.error ?? new Error('Refresh journal transaction aborted.'));
    });
  }

  private open(): Promise<IDBDatabase> {
    return this.database ??= new Promise<IDBDatabase>((resolve, reject) => {
      const request = this.window.indexedDB.open('chessrealms-session', 1);
      request.onupgradeneeded = () => request.result.createObjectStore(storeName);
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const database = request.result;
        database.onversionchange = () => {
          database.close();
          this.database = undefined;
        };
        database.onclose = () => { this.database = undefined; };
        resolve(database);
      };
    }).catch(error => {
      this.database = undefined;
      throw error;
    });
  }
}
