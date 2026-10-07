import { Component, computed, input, output } from '@angular/core';

@Component({
    selector: 'app-saved-flow-graph',
    template: ` <div
        class="saved-graph-scroll"
        role="region"
        aria-label="Saved flow diagram"
        tabindex="0"
    >
        <div
            class="saved-graph-world"
            [style.width.px]="layout().width"
            [style.height.px]="layout().height"
        >
            <svg
                class="saved-graph-edges"
                aria-hidden="true"
                [attr.viewBox]="'0 0 ' + layout().width + ' ' + layout().height"
            >
                <defs>
                    <marker
                        id="flow-arrow"
                        markerWidth="7"
                        markerHeight="7"
                        refX="6"
                        refY="3.5"
                        orient="auto"
                    >
                        <path d="M0 0 L7 3.5 L0 7" />
                    </marker>
                </defs>
                @for (edge of layout().edges; track edge.id) {
                    <path class="saved-edge" [attr.d]="edge.path" marker-end="url(#flow-arrow)" />
                }
            </svg>
            @for (node of layout().nodes; track node.id) {
                <button
                    type="button"
                    class="live-node saved-node"
                    [style.left.px]="node.x"
                    [style.top.px]="node.y"
                    [attr.aria-pressed]="selected() === node.id"
                    (click)="choose.emit(node.id)"
                >
                    <span class="muted">{{
                        node.conditional
                            ? 'Conditional step'
                            : node.after.length
                              ? 'After ' + node.after.join(', ')
                              : 'Start'
                    }}</span>
                    <strong>{{ node.id }}</strong
                    ><code>{{ node.contract.key }}</code>
                    <span class="badge" [attr.data-status]="node.status">{{ node.status }}</span>
                </button>
            }
        </div>
    </div>`,
})
export class SavedFlowGraph {
    readonly steps =
        input.required<
            { id: string; after: string[]; contract: { key: string }; conditional: boolean }[]
        >();
    readonly jobs = input.required<{ stepId: string | null; status: string }[]>();
    readonly selected = input<string | null>(null);
    readonly choose = output<string>();
    readonly layout = computed(() => {
        const steps = this.steps();
        const levels = new Map<string, number>();
        const level = (id: string, visiting = new Set<string>()): number => {
            if (levels.has(id)) return levels.get(id)!;
            if (visiting.has(id)) return 0;
            visiting.add(id);
            const predecessors = steps.find((s) => s.id === id)?.after ?? [];
            const result = predecessors.length
                ? 1 + Math.max(...predecessors.map((p) => level(p, new Set(visiting))))
                : 0;
            levels.set(id, result);
            return result;
        };
        const rows = new Map<number, number>();
        const nodes = steps.map((step) => {
            const column = level(step.id);
            const row = rows.get(column) ?? 0;
            rows.set(column, row + 1);
            return {
                ...step,
                x: 24 + column * 300,
                y: 24 + row * 180,
                status: this.jobs().find((j) => j.stepId === step.id)?.status ?? 'Waiting',
            };
        });
        const edges = nodes.flatMap((node) =>
            node.after.flatMap((id) => {
                const parent = nodes.find((n) => n.id === id);
                if (!parent) return [];
                const x = parent.x + 240,
                    y = parent.y + 72,
                    targetY = node.y + 72;
                return [
                    {
                        id: id + ':' + node.id,
                        path: `M ${x} ${y} C ${x + 30} ${y}, ${node.x - 30} ${targetY}, ${node.x} ${targetY}`,
                    },
                ];
            }),
        );
        return {
            nodes,
            edges,
            width: Math.max(300, ...nodes.map((n) => n.x + 264)),
            height: Math.max(192, ...nodes.map((n) => n.y + 168)),
        };
    });
}
