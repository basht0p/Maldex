import type { ProcessNodeDto } from '../api/types'
import { CopyButton } from '../kit/CopyButton'
import { EmptyState } from '../kit/EmptyState'
import { safeText } from '../util/safeText'
import styles from './ProcessTree.module.css'

interface TreeNode {
  process: ProcessNodeDto
  children: TreeNode[]
}

/** "C:\\Users\\a\\dropper.exe" -> "dropper.exe" (either separator; guest paths are never trusted to be well formed). */
function imageFileName(imagePath: string): string {
  const name = imagePath.split(/[\\/]/).pop()
  return name || imagePath
}

/** Roots are nodes whose parentId is null, unknown, or itself; nodes stranded in a parent cycle are promoted to roots. */
function buildTree(processes: ProcessNodeDto[]): TreeNode[] {
  const nodes = new Map<number, TreeNode>()
  for (const p of processes) nodes.set(p.id, { process: p, children: [] })
  const roots: TreeNode[] = []
  for (const node of nodes.values()) {
    const parentId = node.process.parentId
    const parent = parentId == null || parentId === node.process.id ? undefined : nodes.get(parentId)
    if (parent) parent.children.push(node)
    else roots.push(node)
  }
  // Anything unreachable from a root is part of a cycle (A -> B -> A): show it rather than lose it.
  const reached = new Set<number>()
  const walk = (n: TreeNode) => {
    reached.add(n.process.id)
    n.children.forEach(walk)
  }
  roots.forEach(walk)
  for (const node of nodes.values()) {
    if (reached.has(node.process.id)) continue
    const parent = nodes.get(node.process.parentId as number)
    if (parent) parent.children = parent.children.filter((c) => c !== node)
    roots.push(node)
    walk(node)
  }
  return roots
}

function Node({ node }: { node: TreeNode }) {
  const p = node.process
  const exited = p.endedAt != null
  return (
    <li>
      <div className={`${styles.row} ${exited ? styles.exited : ''}`} title={p.commandLine}>
        <span className={styles.image}>{safeText(imageFileName(p.imagePath))}</span>
        <span className={styles.pid}>pid {p.pid}</span>
        <span className={styles.cmd}>{safeText(p.commandLine)}</span>
        {p.commandLine && <CopyButton value={p.commandLine} label="Copy command line" />}
      </div>
      {node.children.length > 0 && (
        <ul className={styles.list}>
          {node.children.map((c) => (
            <Node key={c.process.id} node={c} />
          ))}
        </ul>
      )}
    </li>
  )
}

/** Process tree. Every guest-derived string is a plain JSX text node (or an attribute), never HTML. */
export function ProcessTree({ processes }: { processes: ProcessNodeDto[] }) {
  if (processes.length === 0) return <EmptyState title="No processes yet." />
  return (
    <ul className={`${styles.list} ${styles.root}`}>
      {buildTree(processes).map((n) => (
        <Node key={n.process.id} node={n} />
      ))}
    </ul>
  )
}

export default ProcessTree
