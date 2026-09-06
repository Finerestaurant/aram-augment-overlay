// 네비의 별 개수와 히어로의 숫자를 GitHub 에서 그때그때 읽어온다.
// 빌드가 없는 사이트라, 릴리스를 올려도 페이지를 다시 배포할 필요가 없다.
//
// 100 미만인 수치는 아예 내보내지 않는다. 갓 시작한 프로젝트의 한 자리 수는
// 신뢰를 주기는커녕 깎아먹는다. 실패했을 때(레이트리밋, 오프라인)와 같은
// 취급이라, 그 자리는 조용히 비어 있게 된다.
(async () => {
  const REPO = 'Finerestaurant/aram-augment-overlay';
  const API = 'https://api.github.com/repos/' + REPO;
  const MIN = 100;

  const get = (url) => fetch(url).then(r => (r.ok ? r.json() : null)).catch(() => null);
  const [repo, releases] = await Promise.all([get(API), get(API + '/releases?per_page=100')]);

  const show = (id, text) => {
    const el = document.getElementById(id);
    if (!el) return;
    el.textContent = text;
    el.closest('[hidden]')?.removeAttribute('hidden');
  };
  const count = (id, n) => {
    if (typeof n !== 'number' || n < MIN) return;
    show(id, n.toLocaleString(undefined, { notation: n >= 10000 ? 'compact' : 'standard' }));
  };

  if (repo) {
    count('stat-stars', repo.stargazers_count);
    count('nav-stars', repo.stargazers_count);
    count('stat-forks', repo.forks_count);
  }

  if (Array.isArray(releases)) {
    // 체크섬 파일은 빼고 exe 만 센다.
    count('stat-downloads', releases
      .flatMap(r => r.assets || [])
      .filter(a => a.name.toLowerCase().endsWith('.exe'))
      .reduce((sum, a) => sum + a.download_count, 0));

    const latest = releases.find(r => !r.draft);   // 버전은 수치가 아니므로 항상 보여준다
    if (latest) show('stat-version', latest.tag_name);
  }
})();
