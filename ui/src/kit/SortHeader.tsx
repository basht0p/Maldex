import styles from './SortHeader.module.css'

export function SortHeader(props: {
  field: string
  label: string
  sort: string
  dir: 'asc' | 'desc'
  onSort: (field: string, dir: 'asc' | 'desc') => void
}) {
  const { field, label, sort, dir, onSort } = props
  const active = sort === field
  const ariaSort = !active ? 'none' : dir === 'asc' ? 'ascending' : 'descending'
  return (
    <th scope="col" aria-sort={ariaSort} className={styles.th}>
      <button
        type="button"
        className={styles.btn}
        onClick={() => onSort(field, active ? (dir === 'asc' ? 'desc' : 'asc') : 'desc')}
      >
        {label}
        {active && <span aria-hidden="true" className={styles.arrow}>{dir === 'asc' ? '▲' : '▼'}</span>}
      </button>
    </th>
  )
}
