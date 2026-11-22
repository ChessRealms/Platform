import { Component, computed, ElementRef, inject, input, output, signal } from '@angular/core';
import { PlayerColor } from './game.models';

const files = 'abcdefgh';
const pieces: Record<string, [string, string]> = {
  K: ['♔', 'White king'], Q: ['♕', 'White queen'], R: ['♖', 'White rook'],
  B: ['♗', 'White bishop'], N: ['♘', 'White knight'], P: ['♙', 'White pawn'],
  k: ['♚', 'Black king'], q: ['♛', 'Black queen'], r: ['♜', 'Black rook'],
  b: ['♝', 'Black bishop'], n: ['♞', 'Black knight'], p: ['♟', 'Black pawn'],
};

@Component({
  selector: 'cr-chess-board',
  template: `
    <div class="board" role="group" aria-label="Chessboard. Use arrow keys to navigate squares."
      (keydown)="navigate($event)">
      @for (square of squares(); track square.name; let i = $index) {
        <button type="button" class="square" [class.dark]="square.dark" [class.selected]="selected() === square.name"
          [class.legal]="targets().has(square.name)" [class.last-move]="lastMove().includes(square.name)"
          [class.occupied]="square.glyph" [attr.data-square]="square.name"
          [attr.aria-label]="square.name + ', ' + square.description"
          [attr.aria-pressed]="selected() === square.name" [attr.tabindex]="tabStop() === square.name ? 0 : -1"
          (focus)="focused.set(square.name)" (click)="squareSelected.emit(square.name)">
          @if (i % 8 === 0) { <span class="rank" aria-hidden="true">{{ square.name[1] }}</span> }
          <span class="piece" aria-hidden="true">{{ square.glyph }}</span>
          @if (i >= 56) { <span class="file" aria-hidden="true">{{ square.name[0] }}</span> }
        </button>
      }
    </div>
  `,
  styleUrl: './chess-board.css',
})
export class ChessBoard {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly fen = input.required<string>();
  readonly color = input.required<PlayerColor>();
  readonly selected = input<string | null>(null);
  readonly targets = input.required<ReadonlySet<string>>();
  readonly lastMove = input<string[]>([]);
  readonly squareSelected = output<string>();
  readonly focused = signal('');

  readonly squares = computed(() => {
    const ranks = this.fen().split(' ')[0].split('/');
    const squares = ranks.flatMap((rank, row) => {
      const expanded = rank.replace(/[1-8]/g, count => '.'.repeat(Number(count)));
      return [...expanded].map((piece, column) => ({
        name: files[column] + (8 - row),
        dark: (row + column) % 2 === 1,
        glyph: pieces[piece]?.[0] ?? '',
        description: pieces[piece]?.[1] ?? 'empty',
      }));
    });
    return this.color() === 'Black' ? squares.reverse() : squares;
  });
  readonly tabStop = computed(() => this.focused() || this.squares()[0]?.name);

  navigate(event: KeyboardEvent): void {
    const index = this.squares().findIndex(square => square.name === this.tabStop());
    let next: number;
    switch (event.key) {
      case 'ArrowLeft': next = index % 8 === 0 ? index : index - 1; break;
      case 'ArrowRight': next = index % 8 === 7 ? index : index + 1; break;
      case 'ArrowUp': next = Math.max(index % 8, index - 8); break;
      case 'ArrowDown': next = Math.min(56 + index % 8, index + 8); break;
      default: return;
    }
    event.preventDefault();
    this.host.nativeElement.querySelector<HTMLElement>(`[data-square="${this.squares()[next]?.name}"]`)?.focus();
  }
}
