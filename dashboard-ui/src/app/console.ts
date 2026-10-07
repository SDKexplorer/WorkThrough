import { ChangeDetectionStrategy, Component, computed, signal } from '@angular/core';
import { DecimalPipe, JsonPipe } from '@angular/common';
import { completionBuckets, recentJobs, steps } from './example-data';
import { FlowGraphComponent } from './flow-graph';

@Component({
    selector: 'app-console',
    imports: [DecimalPipe, JsonPipe, FlowGraphComponent],
    templateUrl: './console.html',
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { '(window:hashchange)': 'syncSection()' },
})
export class ConsoleComponent {
    readonly activeSection = signal(window.location.hash.slice(1) || 'overview');
    readonly light = signal(false);
    readonly successful = signal(false);
    readonly selected = signal(1);
    readonly detailTab = signal('attempts');
    readonly query = signal('');
    readonly status = signal('');
    readonly notice = signal('');
    readonly steps = steps;
    readonly buckets = completionBuckets;
    readonly completed = completionBuckets.reduce((sum, count) => sum + count, 0);
    readonly step = computed(() => steps[this.selected()] ?? steps[0]!);
    readonly stepState = computed(() =>
        this.successful() || this.selected() === 0
            ? 'Succeeded'
            : this.selected() === 1
              ? 'Failed'
              : 'Blocked',
    );
    readonly filteredJobs = computed(() =>
        recentJobs.filter(
            (job) =>
                (!this.status() || job.status === this.status()) &&
                `${job.id} ${job.processor} ${job.correlation}`
                    .toLowerCase()
                    .includes(this.query().trim().toLowerCase()),
        ),
    );

    syncSection(): void {
        this.activeSection.set(window.location.hash.slice(1) || 'overview');
    }

    selectStep(index: number): void {
        this.selected.set(index);
        this.detailTab.set('attempts');
    }

    async copyProcessor(): Promise<void> {
        try {
            await navigator.clipboard.writeText(this.step().processor);
            this.notice.set(`Copied processor ID ${this.step().processor}.`);
        } catch {
            this.notice.set('Clipboard unavailable. Select and copy the visible processor ID.');
        }
    }
}
