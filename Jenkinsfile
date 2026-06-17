def UUID_DIR = UUID.randomUUID().toString()
def buildInfo = Artifactory.newBuildInfo()
def dockerRepository = ''
def registryUrl = ''
def services = []
def version = 'v0.0.0';
def builtImages = [:]   // artifactName -> full docker image ref (для post-описания)

pipeline {
    agent {
        kubernetes {
            inheritFrom 'dotnet10'
            defaultContainer 'dotnet10-builder'
            customWorkspace UUID_DIR
        }
    }

    parameters {
        string(name: 'branch', description: 'branch to build', defaultValue: 'dev')
        string(name: 'artifact_target_type', description: 'SNAPSHOT | RELEASE | BUILD', defaultValue: 'SNAPSHOT')
    }

    options {
        skipStagesAfterUnstable()
        timestamps()
        timeout(time: 25, unit: 'MINUTES')
    }

    stages {
        stage('Git checkout') {
            steps {
               notifyBitbucketWithState 'INPROGRESS'
               script {
                    sh "git checkout ${params.branch}"
               }
            }
        }

        stage('Load pipeline configuration') {
            steps {
                script {
                    def configuration = readJSON(file: 'jenkinsConfiguration.json')
                    dockerRepository = configuration['dockerRepository']
                    registryUrl      = configuration['registryUrl']
                    services         = configuration['services'] ?: []

                    if (services.isEmpty()) {
                        error('jenkinsConfiguration.json: не задан массив services')
                    }

                    echo(
                    """
                    Конфигурация jenkins:
                       dockerRepository = $dockerRepository
                       registryUrl      = $registryUrl
                       services         = ${services.collect { it.artifactName }.join(', ')}
                    """)

                    services.eachWithIndex { svc, i ->
                        def baKeys = (svc.dockerBuildArgs instanceof Map) ? svc.dockerBuildArgs.keySet().join(', ') : '(none)'
                        echo(
                        """
                        [${i + 1}] ${svc.label ?: svc.artifactName}
                           artifactName       = ${svc.artifactName}
                           dockerFilePath     = ${svc.dockerFilePath}
                           dockerBuildTarget  = ${nonBlank(svc.dockerBuildTarget) ?: '(default)'}
                           dockerBuildContext = ${nonBlank(svc.dockerBuildContext) ?: '.'}
                           dotNetProjectName  = ${nonBlank(svc.dotNetProjectName) ?: '(skip .NET build)'}
                           dockerBuildArgs    = ${baKeys}
                        """)
                    }
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Backend (.NET) собирается ДО любых docker build, чтобы упасть
        // раньше на ошибках компиляции и не тащить лишнее в docker context.
        // Frontend (Web) собирается внутри своего Dockerfile (npm ci + vite
        // build в multi-stage), отдельного шага здесь не требует.
        // ────────────────────────────────────────────────────────────────
        stage('Restore && Build .Net') {
            steps {
                notifyBitbucketWithState 'INPROGRESS'
                script {
                    echo "Версия .Net"
                    sh('dotnet --list-sdks')

                    services.each { svc ->
                        def projectName = nonBlank(svc.dotNetProjectName)
                        if (projectName) {
                            echo "Восстановление зависимостей: ${svc.artifactName} (${projectName})"
                            sh("dotnet restore ${projectName} --no-cache --ignore-failed-sources --configfile nuget.config")
                            sh("dotnet build   ${projectName} -c=Release --no-cache --no-restore")
                        } else {
                            echo "Сервис ${svc.artifactName} — .NET-сборка пропущена (frontend / npm-only)"
                        }
                    }
                }
            }
        }

        // Альфа-Artifactory держит Docker-репозитории с суффиксом по типу артефакта:
        //   alfa-building-docker-snapshots.binary.alfabank.ru — для SNAPSHOT
        //   alfa-building-docker-releases.binary.alfabank.ru  — для RELEASE
        // Без суффикса subdomain не зарегистрирован → docker login возвращает 400
        // Bad Request на /v2/ (Артифактори отдаёт 400 для unknown Docker-репо).
        // Логика повторяет эталон service-dev (см. doc_project/132).
        // ⚠️ Это правило не распространяется на NuGet-репо — для NuGet у Альфы
        //    единственное имя `nuget_public` без суффиксов, см. nuget.config.
        stage('Obtain docker repository') {
            steps {
                notifyBitbucketWithState 'INPROGRESS'
                script {
                    if (params.artifact_target_type == 'RELEASE') {
                        dockerRepository = dockerRepository + "-releases"
                    } else {
                        dockerRepository = dockerRepository + "-snapshots"
                    }
                    echo "Docker repository: ${dockerRepository} (artifact_target_type=${params.artifact_target_type})"
                }
            }
        }

        stage('Obtain service version') {
            steps {
                notifyBitbucketWithState 'INPROGRESS'
                script {
                    // .trim() обязателен: любой редактор/Write добавляет trailing \n,
                    // без trim'а конкатенация даёт "v0.0.1\n.14-SNAPSHOT" → docker отказывает
                    // `invalid reference format` (теги не принимают \n).
                    version = "v" + readFile(file: 'version').trim()
                    if(params.branch != 'master')
                    {
                        version = "${version}.${currentBuild.number}"
                    }
                    if(params.artifact_target_type != 'RELEASE')
                    {
                        version = "${version}-SNAPSHOT"
                    }

                    echo "Version: ${version} (одна и та же для всех сервисов)"
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Сборка и публикация Docker-образов — последовательно по services.
        // Порядок: backend (Api) → frontend (Web).
        // Если Web падает, Api уже в registry — frontend можно пересобрать
        // отдельным запуском, не трогая backend.
        // ────────────────────────────────────────────────────────────────
        stage('Docker build && publish') {
            when {
                expression {
                    params.artifact_target_type == 'RELEASE' || params.artifact_target_type == 'SNAPSHOT'
                }
            }
            steps {
                notifyBitbucketWithState 'INPROGRESS'
                script {
                     def gitUrl = steps.sh(returnStdout: true, script: 'git config remote.origin.url').trim()
                     def lastCommitHash = sh(script: 'git log -n 1 --pretty=format:"%H"', returnStdout: true).trim()

                     echo "Docker version: "
                     sh('docker -v')

                     withCredentials([
                        usernamePassword(
                            credentialsId   : 'jenkins-artifactory',
                            usernameVariable: 'USERNAME',
                            passwordVariable: 'PASSWORD'
                        )
                     ]) {
                        echo "User: $USERNAME"
                        // Один docker login на сессию pipeline — оба сервиса пушим в один registry.
                        // Login — на subdomain Docker-репозитория с суффиксом по target_type:
                        //   "alfa-building-docker-snapshots.binary.alfabank.ru" (SNAPSHOT)
                        //   "alfa-building-docker-releases.binary.alfabank.ru"  (RELEASE)
                        // Суффикс добавлен в stage 'Obtain docker repository' выше.
                        //
                        // Синтаксис эталона service-dev (см. doc_project/132): legacy `--password=`
                        // (Basic Auth) — Артифактори этот формат принимает; `--password-stdin`
                        // (X-Registry-Auth) на их docker-listener отдаёт 400 Bad Request.
                        //
                        // $PASSWORD/$USERNAME пробрасываем через shell-env (withCredentials
                        // их инжектирует), не через Groovy-interpolation — это убирает
                        // Jenkins warning «A secret was passed to "sh" using Groovy String
                        // interpolation» и сохраняет маскирование `***` в логах.
                        // ${dockerRepository}/${registryUrl} — НЕ секреты, интерполируем в Groovy.
                        withEnv(["REGISTRY_HOST=${dockerRepository}.${registryUrl}"]) {
                            sh '''
                                docker login --password="$PASSWORD" --username="$USERNAME" "$REGISTRY_HOST"
                            '''
                        }

                        // ⚠️ Build-description парсится платформой Альфы
                        // (PlatformArtifactsClient) по шаблону `KEY:VALUE` БЕЗ пробела
                        // после `:`. Если поставить пробел — value включит лидирующий
                        // пробел, и URL-запрос к artifacts-api получит `%20HASH`,
                        // что валит 500 «artifact not found». Эталон service-dev
                        // строго `key:value`. См. doc 136.
                        def descriptionLines = []
                        descriptionLines << "gitBranche:${params.branch}"
                        descriptionLines << "version:$version"

                        services.eachWithIndex { svc, idx ->
                            def stepNo          = idx + 1
                            def total           = services.size()
                            // Subdomain-based image-ref: <repo>.<host>/<name>:<tag>
                            def dockerImageName = "${dockerRepository}.${registryUrl}/${svc.artifactName}:$version"
                            def buildContext    = nonBlank(svc.dockerBuildContext) ?: '.'
                            def buildTarget     = nonBlank(svc.dockerBuildTarget)
                            def targetArg       = buildTarget ? "--target ${buildTarget}" : ''

                            // Build-args из jenkinsConfiguration.json (поле `dockerBuildArgs`, map).
                            // Используется для проброса URL'ов корп. репозиториев в Dockerfile
                            // (например, `NPM_REGISTRY_ALFALAB_URL` — URL внутреннего npm-репо
                            // Альфы для scope @alfalab/*, dl-cdn зеркал, etc.).
                            // null/пусто → дополнительных --build-arg не добавляем (используются
                            // ARG-дефолты из Dockerfile).
                            def buildArgsStr = ''
                            if (svc.dockerBuildArgs instanceof Map) {
                                svc.dockerBuildArgs.each { k, v ->
                                    def value = nonBlank(v)
                                    if (value) {
                                        buildArgsStr += " --build-arg ${k}='${value}'"
                                    }
                                }
                            }

                            echo "─── [${stepNo}/${total}] ${svc.label ?: svc.artifactName} ───"
                            echo "Сборка - Project: ${svc.artifactName}, Version: ${version}, Target: ${buildTarget ?: '(default)'}, Context: ${buildContext}"
                            if (buildArgsStr) {
                                echo "Build args: ${buildArgsStr.trim()}"
                            }
                            // `--pull` — каждая сборка тянет свежий base-образ из корп. registry.
                            // Без него Docker реюзает локальный кеш base'а (может застрять
                            // на snapshot месячной давности с устаревшими Alpine-пакетами).
                            // Microsoft периодически пересобирает теги `aspnet:10.0-preview-alpine`
                            // при выходе security-update'ов Alpine — `--pull` даёт нам свежий
                            // snapshot, который + наш `apk upgrade` в Dockerfile закрывает CVE.
                            // См. doc 137 v1.1.
                            sh("docker build --pull --no-cache ${targetArg}${buildArgsStr} -f ${svc.dockerFilePath} -t '${dockerImageName}' -t build/${svc.artifactName} -t ${svc.artifactName}:${version} --label 'version=${version}' --label 'service=${svc.artifactName}' ${buildContext}")
                            sh("docker image push '${dockerImageName}'")

                            def dockerImageDigest = getDockerImageDigest(dockerImageName, dockerRepository, registryUrl, svc.artifactName)
                            echo "Image digest ${dockerImageDigest}"

                            pushManifestToArtifactory(
                                                  registryUrl,
                                                  dockerRepository,
                                                  svc.artifactName,
                                                  version,
                                                  USERNAME,
                                                  PASSWORD,
                                                  lastCommitHash,
                                                  params.branch,
                                                  gitUrl,
                                                  svc.label ?: svc.artifactName)

                            echo "Metadata sent for ${svc.artifactName}"

                            def sha1sum = sh(script: "curl -s http://$registryUrl/artifactory/$dockerRepository/${svc.artifactName}/$version/manifest.json | sha1sum | awk '{print \$1}'", returnStdout: true).trim()
                            echo "Manifest checksum ${sha1sum}"

                            builtImages[svc.artifactName] = dockerImageName

                            descriptionLines << ''
                            descriptionLines << "── ${svc.label ?: svc.artifactName} ──"
                            descriptionLines << "dockerImage:http://$registryUrl/artifactory/$dockerRepository/${svc.artifactName}/$version"
                            descriptionLines << "dockerImageDigest:${dockerImageDigest}"
                            descriptionLines << "artifact_app_sha1:${sha1sum}"
                        }

                        currentBuild.description = descriptionLines.join('\n')
                    }
                }
            }
        }
    }

    post {
        failure {
            notifyBitbucketWithState 'FAILED'
        }
        success {
            notifyBitbucketWithState 'SUCCESS'
        }
    }
}

def pushManifestToArtifactory(
    String dockerRegistryUrl,
    String dockerRepository,
    String artifactName,
    String version,
    String artifactoryUsername,
    String artifactoryPassword,
    String lastCommitHash,
    String branchName,
    String gitUrl,
    String platformLabel
    ){
    sh (script:
        """
        curl --request PATCH 'http://$dockerRegistryUrl/artifactory/api/metadata/$dockerRepository/$artifactName/$version/manifest.json' \
             --user  $artifactoryUsername:$artifactoryPassword \
             --header 'Content-Type: application/json' \
             --data-raw '
            {
                "props":
                {
                    "platform"                    : "true",
                    "platform.app"                : "true",
                    "platform.label"              : "$platformLabel",
                    "platform.artifact-type"      : "service",
                    "platform.artifact.name"      : "$artifactName",
                    "platform.deployment.app-name": "$artifactName",
                    "platform.display-name"       : "$artifactName",
                    "platform.deployment.id"      : "${dockerRepository}.${dockerRegistryUrl}/${artifactName}",
                    "platform.service.id"         : "${dockerRepository}.${dockerRegistryUrl}/${artifactName}",
                    "platform.git.branch"         : "$branchName",
                    "platform.git.repo-url"       : "$gitUrl",
                    "platform.git.commit-id"      : "$lastCommitHash",
                    "version"                     : "$version",
                    "vcs.revision"                : "$lastCommitHash",
                    "Module-Origin"               : "$gitUrl",
                    "build.timestamp"             : "${currentBuild.startTimeInMillis}"
                }
            }'
            """
    )
}

// Нормализует значение из jenkinsConfiguration.json.
// readJSON возвращает JSON-null как строку "null", пустые значения — как "".
// Возвращает null для отсутствующих/пустых/литерально-"null" полей,
// иначе — trimmed строку. Так условия `if (nonBlank(x))` работают единообразно.
def nonBlank(value) {
    if (value == null) return null
    def s = value.toString().trim()
    if (s.isEmpty() || s.equalsIgnoreCase('null')) return null
    return s
}

def notifyBitbucketWithState(String state) {
    if ('SUCCESS' == state || 'FAILED' == state) {
        currentBuild.result = state         // Set result of currentBuild !Important!
    }
    notifyBitbucket()
}

def getCurrentTime() {
    return java.time.Instant.now().format(java.time.format.DateTimeFormatter.ofPattern("yyyyMMdd.HHmmss"))
}

def getDockerImageDigest(
    String dockerImageName,
    String dockerRepositoryName,
    String dockerRegistryUrl,
    String artifactName
    ){
    // RepoDigests возвращает что-то вроде "<repo>.<host>/<name>@sha256:abc..."
    // Отрезаем prefix до '@', оставляем чистый digest.
    return sh(
            returnStdout: true,
            script: "docker inspect --format='{{index .RepoDigests 0}}' $dockerImageName"
        )
        .trim()
        .replace("${dockerRepositoryName}.${dockerRegistryUrl}/$artifactName" + '@', '')
}