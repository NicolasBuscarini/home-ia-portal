# Instruções do projeto garage-watch-platform

## Autoridade e escopo
- Repositório independente para a fundação do produto T01 e instrumentação observável T01-O; o protótipo `garage-watch` continua experimento T00a isolado.
- A autoridade de produto/arquitetura é `../../docs/` no repositório de contexto. Não contrariar ADRs sem registrar nova ADR antes ou com a implementação.
- T00-S está concluída; T00-H segue pendente e bloqueia somente o piloto real. Não alegar compatibilidade de câmera/NAS, desempenho, precisão ou validação 24/7.
- O perfil `camera-integration` é laboratório opt-in: consome o main stream Neolink pela rede Docker interna `garage-watch-rtsp-lab`; não usar credenciais na URI nem expor RTSP/API na LAN. Testar somente um consumidor por vez até validar concorrência.
- O profile Compose `web-dev` é dev-only e bound a loopback; HMR usa o clone irmão `../home-ia-web`. Via `./garage`, o build estático/API também usa esse clone como build context, para `./garage up` empacotar a source atual. O submodule/fallback `web/` foi removido; Compose direto da platform exige `WEB_SOURCE_CONTEXT=../home-ia-web` (default atual do compose) e o pacote irmão `../home-ia-shared` disponível no build. O Vite não depende de/inicia a API; o wrapper seleciona `VITE_API_PROXY_TARGET` para a única API Platform já ativa e nunca recria a captura ao ligar apenas HMR.
- Implementar a base T01 e incrementos autorizados no backlog. RTSP só é habilitado nos perfis `synthetic`, `rtsp-contract` e `camera-integration`; não implementar IA, reconhecimento, eventos completos, NAS, UI Android ou automação física sem tarefa desbloqueada.
- `T16-B` inicia o papel de portal no host atual: catálogo de módulos/menus, navegação autorizada, preferências de tema/idioma e decisão de autorização por módulo/escopo. Desde a ADR109, `remote` é o padrão do portal (`MONITORING_EXECUTION_MODE=remote`, `CAPTURE_AUTHORITY=remote`, `MONITORING_*_SOURCE=remote`): o serviço `home-ia-monitoring` é dono de câmera/spool/clipes/journal/análise/roster/retenção/biometria cifrada/HLS, e o portal mantém trigger, autorização, manifesto/estado do clipe e projeção de recursos (ADR108). O caminho `in-process`/`local` e a implementação embutida de Monitoramento são **rollback-only** (marcador no código e seção do README), sem funcionalidade nova, com remoção planejada em `T16-REMOVE-EMBEDDED`; rollback exige o serviço de Monitoramento parado para não haver dois writers no mesmo spool. Token serviço-a-serviço P28: HS256 de 60 s com `MONITORING_SERVICE_TOKEN_SECRET`; o portal falha na inicialização se alguma fonte remota estiver ativa sem segredo.

## Engenharia
- .NET 10/C# e Docker Compose são a base inicial conforme contexto; imagem, pacotes e ferramentas devem ser fixados e documentados.
- Preserve os padrões e arquitetura existentes e use práticas atuais e suportadas do ecossistema .NET; mudanças coesas e revisáveis, sem framework/dependência por moda ou abstração especulativa. UI/UX é comportamento: acessibilidade, teclado/foco, responsividade, estados claros de vazio/carregamento/erro/sucesso e confirmação para ações destrutivas.
- Antes de assumir decisão material de arquitetura, persistência, segurança, produto ou UX, investigue código/docs, recomende uma opção com trade-offs e pergunte ao operador. Para detalhe reversível de baixo risco, documente default sensato e prossiga.
- Manter Domain/Application independentes de HTTP, persistência e SDKs de fornecedor. Introduzir portas somente em fronteiras concretas.
- Configuração por variáveis de ambiente; exemplos sem segredos. Nunca versionar `.env`, mídia, embeddings, logs sensíveis ou dados locais.
- Testes devem exercitar contratos e fluxo real interno; adaptadores fake somente nas fronteiras externas.
- Observabilidade deve usar dimensões limitadas, não registrar segredos/payload e nunca bloquear o fluxo do caso de uso; exportador/backend não é dependência de readiness.
- Atualizar documentação local e compartilhada, ADR, contratos e Mermaid canônico quando afetados.

## Verificações
- Executar formatação, build, testes, validação Compose e análise estática disponíveis em container. Não exigir instalação local de SDK para uso do projeto.
