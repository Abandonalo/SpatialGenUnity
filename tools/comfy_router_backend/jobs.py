"""In-process registry for refinements that run in the background.

A refinement takes minutes. Holding the HTTP connection open for that long is fragile
over the Colab tunnel, so ``POST /refine`` returns immediately with ``status="queued"``
and Unity polls ``GET /refine/{request_id}`` until the job settles.
"""

from __future__ import annotations

import threading
from typing import Callable, Dict

from .models import MultiViewRefinementResponse


class RefinementJobs:
    """Thread-safe map of request id to its latest response snapshot."""

    def __init__(self, max_tracked: int = 25) -> None:
        self._lock = threading.Lock()
        self._jobs: Dict[str, MultiViewRefinementResponse] = {}
        self._order: list[str] = []
        self._max_tracked = max_tracked

    def start(
        self,
        request_id: str,
        work: Callable[[], MultiViewRefinementResponse],
    ) -> MultiViewRefinementResponse:
        """Registers the job as queued and runs ``work`` on a worker thread."""
        queued = MultiViewRefinementResponse(requestId=request_id, success=True, status="queued")
        self._put(request_id, queued)

        def run() -> None:
            self._put(
                request_id,
                MultiViewRefinementResponse(requestId=request_id, success=True, status="running"),
            )
            try:
                self._put(request_id, work())
            except Exception as exc:  # noqa: BLE001 - reported to the client verbatim
                self._put(
                    request_id,
                    MultiViewRefinementResponse(
                        requestId=request_id, success=False, status="error", errorMessage=str(exc)
                    ),
                )

        threading.Thread(target=run, name=f"refine-{request_id[:8]}", daemon=True).start()
        return queued

    def get(self, request_id: str) -> MultiViewRefinementResponse:
        with self._lock:
            job = self._jobs.get(request_id)

        if job is not None:
            return job

        return MultiViewRefinementResponse(
            requestId=request_id,
            success=False,
            status="error",
            errorMessage=f"No refinement is tracked for request '{request_id}'.",
        )

    def _put(self, request_id: str, response: MultiViewRefinementResponse) -> None:
        with self._lock:
            if request_id not in self._jobs:
                self._order.append(request_id)
            self._jobs[request_id] = response

            # Results carry base64 meshes, so old jobs are evicted rather than kept forever.
            while len(self._order) > self._max_tracked:
                self._jobs.pop(self._order.pop(0), None)
