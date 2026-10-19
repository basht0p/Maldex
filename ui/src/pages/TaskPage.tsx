import { useParams } from 'react-router-dom'

/** Placeholder: the live task page (process tree, events, controls) is added in Task 18. */
export default function TaskPage() {
  const { id } = useParams()
  return (
    <section>
      <h1>Task</h1>
      <p>Task {id}: the live view arrives in the next step.</p>
    </section>
  )
}
