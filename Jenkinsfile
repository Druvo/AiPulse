pipeline {
    agent any
    
    environment {
        REMOTE_HOST = '192.168.0.50'
        REMOTE_USER = 'druvo'
        REMOTE_PATH = '/opt/aipulse'
        SERVICE_NAME = 'aipulse'
    }
    
    stages {
        stage('Checkout') {
            steps {
                checkout scm
            }
        }
        
        stage('Build') {
            steps {
                bat 'dotnet publish -c Release -o publish --self-contained false -r linux-x64'
            }
        }
        
        stage('Backup on Corex') {
            steps {
                sshagent(['corex-ssh']) {
                    bat """
                        ssh -o StrictHostKeyChecking=no %REMOTE_USER%@%REMOTE_HOST% "sudo tar czf /tmp/aipulse-backup-%BUILD_ID%.tar.gz -C /opt aipulse 2>/dev/null || true"
                    """
                }
            }
        }
        
        stage('Deploy to Corex') {
            steps {
                sshagent(['corex-ssh']) {
                    bat """
                        ssh -o StrictHostKeyChecking=no %REMOTE_USER%@%REMOTE_HOST% "sudo mkdir -p %REMOTE_PATH%"
                        scp -o StrictHostKeyChecking=no -r publish/* %REMOTE_USER%@%REMOTE_HOST%:/tmp/aipulse-deploy/
                        ssh -o StrictHostKeyChecking=no %REMOTE_USER%@%REMOTE_HOST% "sudo cp -r /tmp/aipulse-deploy/* %REMOTE_PATH%/ && rm -rf /tmp/aipulse-deploy"
                    """
                }
            }
        }
        
        stage('Restart Service') {
            steps {
                sshagent(['corex-ssh']) {
                    bat """
                        ssh -o StrictHostKeyChecking=no %REMOTE_USER%@%REMOTE_HOST% "sudo systemctl restart %SERVICE_NAME%"
                        ssh -o StrictHostKeyChecking=no %REMOTE_USER%@%REMOTE_HOST% "sleep 3 && sudo systemctl is-active %SERVICE_NAME%"
                    """
                }
            }
        }
        
        stage('Verify') {
            steps {
                sshagent(['corex-ssh']) {
                    bat """
                        ssh -o StrictHostKeyChecking=no %REMOTE_USER%@%REMOTE_HOST% "curl -s -o /dev/null -w '%%{http_code}' http://localhost:5257/"
                    """
                }
            }
        }
    }
    
    post {
        failure {
            echo 'AiPulse deployment FAILED! Backup available at /tmp/aipulse-backup-${BUILD_ID}.tar.gz on corex'
        }
        success {
            echo 'AiPulse deployed successfully to https://aipulse.druvium.xyz'
        }
    }
}
