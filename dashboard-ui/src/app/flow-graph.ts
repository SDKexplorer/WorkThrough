import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { ExampleStep, StepState } from './example-data';

@Component({
    selector: 'app-flow-graph',
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: ` <ol class="flow-nodes" aria-label="Saved flow steps in execution order">
        @for (step of steps(); track step.id; let index = $index) {
            <li [class.path-complete]="state(index) === 'Succeeded'">
                <button
                    type="button"
                    class="flow-node"
                    [class.selected]="selected() === index"
                    [attr.aria-pressed]="selected() === index"
                    [attr.data-state]="state(index)"
                    (click)="selectStep.emit(index)"
                    [attr.aria-label]="step.name + ', ' + state(index)"
                >
                    <span class="node-heading"
                        ><span class="node-number">{{ index + 1 }}</span
                        ><span class="badge" [attr.data-state]="state(index)">{{
                            state(index)
                        }}</span></span
                    >
                    <strong>{{ step.name }}</strong>
                    <code>{{ step.processor }}</code>
                    <span class="node-time">{{
                        state(index) === 'Blocked' ? 'Waiting for predecessor' : step.duration
                    }}</span>
                </button>
            </li>
        }
    </ol>`,
})
export class FlowGraphComponent {
    readonly steps = input.required<readonly ExampleStep[]>();
    readonly successful = input(false);
    readonly selected = input(1);
    readonly selectStep = output<number>();

    state(index: number): StepState {
        return this.successful() || index === 0 ? 'Succeeded' : index === 1 ? 'Failed' : 'Blocked';
    }
}
